using System;
using System.Activities;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Sdk.Workflow;

namespace RCRC.CRM.WorkflowActivities
{
    public class GetTeamCachedEmails : CodeActivity
    {
        private const string TeamEntityName = "team";
        private const string TeamCachedEmailsField = "crm2p_cachedemails";
        private const string TeamCachedEmailsDateField = "crm2p_cachedemailson";

        [RequiredArgument]
        [Input("Team")]
        [ReferenceTarget("team")]
        public InArgument<EntityReference> Team { get; set; }

        [Input("Force Refresh")]
        public InArgument<bool> ForceRefresh { get; set; }

        [Output("Emails")]
        public OutArgument<string> Emails { get; set; }

        [Output("Was Refreshed")]
        public OutArgument<bool> WasRefreshed { get; set; }

        protected override void Execute(CodeActivityContext context)
        {
            var tracing = context.GetExtension<ITracingService>();
            var workflowContext = context.GetExtension<IWorkflowContext>();
            var serviceFactory = context.GetExtension<IOrganizationServiceFactory>();
            var service = serviceFactory.CreateOrganizationService(workflowContext.UserId);

            var teamRef = Team.Get(context);
            var forceRefresh = ForceRefresh.Get(context);

            if (teamRef == null || teamRef.Id == Guid.Empty)
                throw new InvalidPluginExecutionException("Team is required.");

            if (!string.Equals(teamRef.LogicalName, TeamEntityName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidPluginExecutionException("Input reference must be a Team.");

            tracing.Trace("GetTeamCachedEmails started. TeamId={0}, ForceRefresh={1}", teamRef.Id, forceRefresh);

            var today = GetRiyadhToday();

            var team = service.Retrieve(
                TeamEntityName,
                teamRef.Id,
                new ColumnSet(TeamCachedEmailsField, TeamCachedEmailsDateField));

            var cachedEmails = team.GetAttributeValue<string>(TeamCachedEmailsField) ?? string.Empty;
            var cachedDate = team.GetAttributeValue<DateTime?>(TeamCachedEmailsDateField);

            bool cacheValid =
                !forceRefresh &&
                !string.IsNullOrWhiteSpace(cachedEmails) &&
                cachedDate.HasValue &&
                cachedDate.Value.Date == today.Date;

            if (cacheValid)
            {
                tracing.Trace("Team email cache is valid. Returning cached emails.");
                Emails.Set(context, cachedEmails);
                WasRefreshed.Set(context, false);
                return;
            }

            tracing.Trace("Team email cache is missing/expired. Refreshing from team members.");

            var freshEmails = FetchActiveTeamMemberEmails(service, teamRef.Id, tracing);
            var emailCsv = string.Join(",", freshEmails);

            var updateTeam = new Entity(TeamEntityName, teamRef.Id);
            updateTeam[TeamCachedEmailsField] = emailCsv;
            updateTeam[TeamCachedEmailsDateField] = today;

            service.Update(updateTeam);

            tracing.Trace("Team email cache updated. EmailCount={0}", freshEmails.Count);

            Emails.Set(context, emailCsv);
            WasRefreshed.Set(context, true);
        }

        private static DateTime GetRiyadhToday()
        {
            // Saudi Arabia is UTC+3 and has no daylight saving time.
            return DateTime.UtcNow.AddHours(3).Date;
        }

        private static List<string> FetchActiveTeamMemberEmails(
            IOrganizationService service,
            Guid teamId,
            ITracingService tracing)
        {
            var query = new QueryExpression("systemuser")
            {
                ColumnSet = new ColumnSet("internalemailaddress", "isdisabled"),
                NoLock = true
            };

            query.Criteria.AddCondition("isdisabled", ConditionOperator.Equal, false);
            query.Criteria.AddCondition("internalemailaddress", ConditionOperator.NotNull);

            var teamMembershipLink = query.AddLink(
                "teammembership",
                "systemuserid",
                "systemuserid",
                JoinOperator.Inner);

            teamMembershipLink.LinkCriteria.AddCondition("teamid", ConditionOperator.Equal, teamId);

            var results = service.RetrieveMultiple(query);

            var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var user in results.Entities)
            {
                var email = user.GetAttributeValue<string>("internalemailaddress");

                if (string.IsNullOrWhiteSpace(email))
                    continue;

                email = email.Trim();

                if (!email.Contains("@"))
                {
                    tracing.Trace("Skipping invalid email: {0}", email);
                    continue;
                }

                emails.Add(email);
            }

            return emails.OrderBy(e => e).ToList();
        }
    }
}