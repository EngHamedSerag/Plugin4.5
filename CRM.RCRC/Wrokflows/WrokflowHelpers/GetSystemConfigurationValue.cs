using System;
using System.Activities;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Sdk.Workflow;

namespace RCRC.CRM.WorkflowActivities
{
    public class GetSystemConfigurationValue : CodeActivity
    {
        [RequiredArgument]
        [Input("Configuration Name")]
        public InArgument<string> ConfigurationName { get; set; }

        [Output("Configuration Value")]
        public OutArgument<string> ConfigurationValue { get; set; }

        protected override void Execute(CodeActivityContext context)
        {
            ITracingService tracingService =
                context.GetExtension<ITracingService>();

            IWorkflowContext workflowContext =
                context.GetExtension<IWorkflowContext>();

            IOrganizationServiceFactory serviceFactory =
                context.GetExtension<IOrganizationServiceFactory>();

            IOrganizationService service =
                serviceFactory.CreateOrganizationService(workflowContext.UserId);

            try
            {
                string configName = ConfigurationName.Get(context);

                if (string.IsNullOrWhiteSpace(configName))
                {
                    ConfigurationValue.Set(context, string.Empty);
                    return;
                }

                tracingService.Trace("Fetching crm2p_systemconfiguration by name: " + configName);

                QueryExpression query = new QueryExpression("crm2p_systemconfiguration")
                {
                    ColumnSet = new ColumnSet("crm2p_value"),
                    TopCount = 1
                };

                query.Criteria.AddCondition(
                    "crm2p_name",
                    ConditionOperator.Equal,
                    configName);

                EntityCollection results = service.RetrieveMultiple(query);

                if (results.Entities.Count > 0)
                {
                    string value = results.Entities[0].GetAttributeValue<string>("crm2p_value");
                    ConfigurationValue.Set(context, value ?? string.Empty);

                    tracingService.Trace("Configuration found. Value: " + value);
                }
                else
                {
                    tracingService.Trace("Configuration not found.");
                    ConfigurationValue.Set(context, string.Empty);
                }
            }
            catch (Exception ex)
            {
                tracingService.Trace("Error in GetSystemConfigurationValue: " + ex.ToString());
                throw new InvalidPluginExecutionException(
                    "Failed to fetch system configuration.", ex);
            }
        }
    }
}