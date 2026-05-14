using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Sdk.Workflow;
using System;
using System.Activities;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CRM.WorkflowActivities
{
    public class SyncCaseQuestionnaireFromAnswers : CodeActivity
    {
        private const string CaseEntityName = "incident";

        // Field on Case
        private const string CaseQuestionnaireField = "crm2p_questionnaire";

        // Answer entity
        private const string AnswerEntityName = "crm2p_answer";

        // Answer fields
        private const string AnswerPrimaryIdField = "crm2p_answerid";
        private const string AnswerCaseLookup = "crm2p_case";
        private const string AnswerQuestionLookup = "crm2p_question";

        // Based on your OData sample, the actual answer value is stored in crm2p_name
        private const string AnswerNameField = "crm2p_name";

        // Optional, kept as fallback
        private const string AnswerValueField = "crm2p_value";

        private const string QuestionEntityName = "crm2p_question";

        private const string QuestionSeparator = "###";
        private const string AnswerSeparator = "$$$";

        protected override void Execute(CodeActivityContext executionContext)
        {
            ITracingService tracing =
                executionContext.GetExtension<ITracingService>();

            IWorkflowContext context =
                executionContext.GetExtension<IWorkflowContext>();

            IOrganizationServiceFactory serviceFactory =
                executionContext.GetExtension<IOrganizationServiceFactory>();

            IOrganizationService service =
                serviceFactory.CreateOrganizationService(context.UserId);

            try
            {
                if (!string.Equals(context.PrimaryEntityName, CaseEntityName, StringComparison.OrdinalIgnoreCase))
                {
                    tracing.Trace("This activity must run on incident. Current entity: {0}", context.PrimaryEntityName);
                    return;
                }

                Guid caseId = context.PrimaryEntityId;

                tracing.Trace("Starting questionnaire sync for case: {0}", caseId);

                Entity caseRecord = service.Retrieve(
                    CaseEntityName,
                    caseId,
                    new ColumnSet(CaseQuestionnaireField)
                );

                string questionnaire = GetString(caseRecord, CaseQuestionnaireField);

                List<Entity> answers = RetrieveCaseAnswers(service, caseId, tracing);

                bool questionnaireIsEmpty = string.IsNullOrWhiteSpace(questionnaire);
                bool answersExist = answers.Count > 0;

                tracing.Trace("Questionnaire empty: {0}", questionnaireIsEmpty);
                tracing.Trace("Answers exist: {0}", answersExist);
                tracing.Trace("Answer count: {0}", answers.Count);

                if (questionnaireIsEmpty && answersExist)
                {
                    tracing.Trace("Building crm2p_questionnaire from crm2p_answer records.");

                    string generatedQuestionnaire =
                        PrepareQuestionnaireFromAnswers(answers, tracing);

                    if (!string.IsNullOrWhiteSpace(generatedQuestionnaire))
                    {
                        Entity updateCase = new Entity(CaseEntityName);
                        updateCase.Id = caseId;
                        updateCase[CaseQuestionnaireField] = generatedQuestionnaire;

                        service.Update(updateCase);

                        tracing.Trace("Case crm2p_questionnaire updated.");
                    }

                    return;
                }

                if (!questionnaireIsEmpty && !answersExist)
                {
                    tracing.Trace("Creating crm2p_answer records from crm2p_questionnaire.");

                    List<QuestionnaireAnswerItem> parsedItems =
                        ParseQuestionnaire(questionnaire, tracing);

                    tracing.Trace("Parsed answer items: {0}", parsedItems.Count);

                    foreach (QuestionnaireAnswerItem item in parsedItems)
                    {
                        CreateAnswer(service, caseId, item, tracing);
                    }

                    tracing.Trace("Answer creation completed.");
                    return;
                }

                tracing.Trace("No sync needed.");
            }
            catch (Exception ex)
            {
                tracing.Trace("SyncCaseQuestionnaireFromAnswers failed: {0}", ex.ToString());

                throw new InvalidPluginExecutionException(
                    "Failed to sync questionnaire and answers: " + ex.Message,
                    ex
                );
            }
        }

        private static List<Entity> RetrieveCaseAnswers(
            IOrganizationService service,
            Guid caseId,
            ITracingService tracing)
        {
            QueryExpression query = new QueryExpression(AnswerEntityName)
            {
                ColumnSet = new ColumnSet(
                    AnswerPrimaryIdField,
                    AnswerCaseLookup,
                    AnswerQuestionLookup,
                    AnswerNameField,
                    AnswerValueField,
                    "createdon"
                )
            };

            query.Criteria.AddCondition(
                AnswerCaseLookup,
                ConditionOperator.Equal,
                caseId
            );

            query.Orders.Add(new OrderExpression("createdon", OrderType.Ascending));

            EntityCollection result = service.RetrieveMultiple(query);

            tracing.Trace("Retrieved crm2p_answer count: {0}", result.Entities.Count);

            return result.Entities.ToList();
        }

        private static string PrepareQuestionnaireFromAnswers(
            List<Entity> answerRecords,
            ITracingService tracing)
        {
            /*
             * Final format:
             *
             * questionId$$$answer
             * questionId$$$answer1$$$answer2
             * questionId$$$answer###questionId2$$$answer
             */

            Dictionary<Guid, List<string>> grouped =
                new Dictionary<Guid, List<string>>();

            foreach (Entity answer in answerRecords)
            {
                EntityReference questionRef = GetEntityReference(answer, AnswerQuestionLookup);

                if (questionRef == null)
                {
                    tracing.Trace("Skipping crm2p_answer {0}. Missing crm2p_question.", answer.Id);
                    continue;
                }

                string answerValue = GetAnswerText(answer);

                if (!grouped.ContainsKey(questionRef.Id))
                {
                    grouped[questionRef.Id] = new List<string>();
                }

                grouped[questionRef.Id].Add(answerValue);
            }

            StringBuilder result = new StringBuilder();
            int count = 0;

            foreach (KeyValuePair<Guid, List<string>> item in grouped)
            {
                if (count > 0)
                {
                    result.Append(QuestionSeparator);
                }

                result.Append(item.Key.ToString());

                foreach (string answerValue in item.Value)
                {
                    result.Append(AnswerSeparator);
                    result.Append(answerValue ?? string.Empty);
                }

                count++;
            }

            return result.ToString();
        }

        private static List<QuestionnaireAnswerItem> ParseQuestionnaire(
            string questionnaire,
            ITracingService tracing)
        {
            List<QuestionnaireAnswerItem> result =
                new List<QuestionnaireAnswerItem>();

            if (string.IsNullOrWhiteSpace(questionnaire))
            {
                return result;
            }

            string[] questionBlocks = questionnaire.Split(
                new string[] { QuestionSeparator },
                StringSplitOptions.RemoveEmptyEntries
            );

            foreach (string block in questionBlocks)
            {
                string[] parts = block.Split(
                    new string[] { AnswerSeparator },
                    StringSplitOptions.None
                );

                if (parts.Length < 2)
                {
                    tracing.Trace("Skipping invalid questionnaire block: {0}", block);
                    continue;
                }

                Guid questionId;

                if (!Guid.TryParse(parts[0], out questionId))
                {
                    tracing.Trace("Skipping block with invalid question id: {0}", parts[0]);
                    continue;
                }

                for (int i = 1; i < parts.Length; i++)
                {
                    result.Add(new QuestionnaireAnswerItem
                    {
                        QuestionId = questionId,
                        AnswerValue = parts[i] ?? string.Empty
                    });
                }
            }

            return result;
        }

        private static void CreateAnswer(
            IOrganizationService service,
            Guid caseId,
            QuestionnaireAnswerItem item,
            ITracingService tracing)
        {
            Entity answer = new Entity(AnswerEntityName);

            answer[AnswerCaseLookup] =
                new EntityReference(CaseEntityName, caseId);

            answer[AnswerQuestionLookup] =
                new EntityReference(QuestionEntityName, item.QuestionId);

            // Based on your OData sample:
            // crm2p_name contains the actual answer text.
            answer[AnswerNameField] = item.AnswerValue ?? string.Empty;

            // Optional: also fill crm2p_value if your forms/API expect it.
            // Keep this enabled if crm2p_value is text.
            answer[AnswerValueField] = item.AnswerValue ?? string.Empty;

            Guid createdId = service.Create(answer);

            tracing.Trace(
                "Created crm2p_answer {0}. Question: {1}. Value: {2}",
                createdId,
                item.QuestionId,
                item.AnswerValue
            );
        }

        private static string GetAnswerText(Entity answer)
        {
            /*
             * Your sample shows crm2p_name = "0534119084"
             * and crm2p_value = null.
             *
             * So priority:
             * 1. crm2p_name
             * 2. crm2p_value
             */

            string name = GetString(answer, AnswerNameField);

            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }

            return GetString(answer, AnswerValueField);
        }

        private static string GetString(Entity entity, string attributeName)
        {
            if (entity == null)
            {
                return string.Empty;
            }

            if (!entity.Contains(attributeName))
            {
                return string.Empty;
            }

            if (entity[attributeName] == null)
            {
                return string.Empty;
            }

            object value = entity[attributeName];

            if (value is OptionSetValue)
            {
                return ((OptionSetValue)value).Value.ToString();
            }

            if (value is EntityReference)
            {
                return ((EntityReference)value).Id.ToString();
            }

            if (value is Money)
            {
                return ((Money)value).Value.ToString();
            }

            if (value is DateTime)
            {
                return ((DateTime)value).ToString("o");
            }

            if (value is bool)
            {
                return ((bool)value) ? "true" : "false";
            }

            return value.ToString();
        }

        private static EntityReference GetEntityReference(Entity entity, string attributeName)
        {
            if (entity == null)
            {
                return null;
            }

            if (!entity.Contains(attributeName))
            {
                return null;
            }

            return entity[attributeName] as EntityReference;
        }

        private class QuestionnaireAnswerItem
        {
            public Guid QuestionId { get; set; }
            public string AnswerValue { get; set; }
        }
    }
}
