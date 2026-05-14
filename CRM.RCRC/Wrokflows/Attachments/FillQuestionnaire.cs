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
        // ============================================================
        // CHANGE THESE BASED ON YOUR ACTUAL CRM SCHEMA
        // ============================================================

        private const string CaseEntityName = "incident";

        // Field on Case
        private const string CaseQuestionnaireField = "crm2p_questionnaire";

        // Child Answer entity
        private const string AnswerEntityName = "crm2p_answer";

        // Lookup from Answer entity to Case
        private const string AnswerCaseLookup = "crm2p_case";

        // Lookup from Answer entity to Question
        private const string AnswerQuestionLookup = "crm2p_question";

        // Text/value field on Answer entity
        private const string AnswerValueField = "crm2p_answer";

        // Optional ordering field if available.
        // If you do not have this field, leave it null.
        private const string AnswerOrderField = null;
        // Example:
        // private const string AnswerOrderField = "createdon";

        // Separator format
        private const string QuestionSeparator = "###";
        private const string AnswerSeparator = "$$$";

        protected override void Execute(CodeActivityContext executionContext)
        {
            ITracingService tracingService =
                executionContext.GetExtension<ITracingService>();

            IWorkflowContext workflowContext =
                executionContext.GetExtension<IWorkflowContext>();

            IOrganizationServiceFactory serviceFactory =
                executionContext.GetExtension<IOrganizationServiceFactory>();

            IOrganizationService service =
                serviceFactory.CreateOrganizationService(workflowContext.UserId);

            try
            {
                if (!string.Equals(workflowContext.PrimaryEntityName, CaseEntityName, StringComparison.OrdinalIgnoreCase))
                {
                    tracingService.Trace("This workflow activity must run on incident/case.");
                    return;
                }

                Guid caseId = workflowContext.PrimaryEntityId;

                tracingService.Trace("Starting questionnaire sync for case: {0}", caseId);

                Entity caseRecord = service.Retrieve(
                    CaseEntityName,
                    caseId,
                    new ColumnSet(CaseQuestionnaireField)
                );

                string questionnaireValue = GetString(caseRecord, CaseQuestionnaireField);

                List<Entity> existingAnswers = RetrieveCaseAnswers(service, caseId, tracingService);

                bool questionnaireIsEmpty = string.IsNullOrWhiteSpace(questionnaireValue);
                bool answersExist = existingAnswers != null && existingAnswers.Count > 0;

                tracingService.Trace("Questionnaire empty: {0}", questionnaireIsEmpty);
                tracingService.Trace("Existing answers count: {0}", existingAnswers.Count);

                if (questionnaireIsEmpty && answersExist)
                {
                    tracingService.Trace("Questionnaire is empty and answers exist. Building questionnaire from answers.");

                    string preparedQuestionnaire = PrepareQuestionnaireFromAnswerRecords(existingAnswers, tracingService);

                    if (!string.IsNullOrWhiteSpace(preparedQuestionnaire))
                    {
                        Entity updateCase = new Entity(CaseEntityName);
                        updateCase.Id = caseId;
                        updateCase[CaseQuestionnaireField] = preparedQuestionnaire;

                        service.Update(updateCase);

                        tracingService.Trace("Case questionnaire updated successfully.");
                    }
                    else
                    {
                        tracingService.Trace("Prepared questionnaire is empty. No update done.");
                    }

                    return;
                }

                if (!questionnaireIsEmpty && !answersExist)
                {
                    tracingService.Trace("Questionnaire is filled and no answers exist. Creating answers from questionnaire.");

                    List<QuestionnaireAnswerItem> parsedAnswers =
                        ParseQuestionnaire(questionnaireValue, tracingService);

                    tracingService.Trace("Parsed answer items count: {0}", parsedAnswers.Count);

                    foreach (QuestionnaireAnswerItem item in parsedAnswers)
                    {
                        CreateAnswerRecord(service, caseId, item, tracingService);
                    }

                    tracingService.Trace("Answer records created successfully.");
                    return;
                }

                tracingService.Trace("No action needed.");
            }
            catch (Exception ex)
            {
                tracingService.Trace("SyncCaseQuestionnaireFromAnswers failed: {0}", ex.ToString());
                throw new InvalidPluginExecutionException(
                    "Failed to sync case questionnaire and answers: " + ex.Message,
                    ex
                );
            }
        }

        private static List<Entity> RetrieveCaseAnswers(
            IOrganizationService service,
            Guid caseId,
            ITracingService tracingService)
        {
            QueryExpression query = new QueryExpression(AnswerEntityName)
            {
                ColumnSet = new ColumnSet(
                    AnswerQuestionLookup,
                    AnswerValueField
                )
            };

            query.Criteria.AddCondition(
                AnswerCaseLookup,
                ConditionOperator.Equal,
                caseId
            );

            if (!string.IsNullOrWhiteSpace(AnswerOrderField))
            {
                query.Orders.Add(new OrderExpression(AnswerOrderField, OrderType.Ascending));
            }

            EntityCollection result = service.RetrieveMultiple(query);

            tracingService.Trace("Retrieved answers count: {0}", result.Entities.Count);

            return result.Entities.ToList();
        }

        private static string PrepareQuestionnaireFromAnswerRecords(
            List<Entity> answerRecords,
            ITracingService tracingService)
        {
            /*
             * Output format:
             *
             * questionId$$$answer
             * questionId$$$answer1$$$answer2
             *
             * Records are grouped by question.
             * If the same question has multiple answer records,
             * they become a multi-answer segment.
             */

            Dictionary<Guid, List<string>> groupedAnswers =
                new Dictionary<Guid, List<string>>();

            foreach (Entity answerRecord in answerRecords)
            {
                EntityReference questionRef = GetEntityReference(answerRecord, AnswerQuestionLookup);

                if (questionRef == null)
                {
                    tracingService.Trace("Skipping answer record {0}. Missing question lookup.", answerRecord.Id);
                    continue;
                }

                string answerValue = GetAnswerValueAsString(answerRecord, AnswerValueField);

                if (!groupedAnswers.ContainsKey(questionRef.Id))
                {
                    groupedAnswers[questionRef.Id] = new List<string>();
                }

                groupedAnswers[questionRef.Id].Add(answerValue);
            }

            StringBuilder result = new StringBuilder();
            int count = 0;

            foreach (KeyValuePair<Guid, List<string>> questionGroup in groupedAnswers)
            {
                if (count > 0)
                {
                    result.Append(QuestionSeparator);
                }

                result.Append(questionGroup.Key.ToString());

                foreach (string answer in questionGroup.Value)
                {
                    result.Append(AnswerSeparator);
                    result.Append(answer ?? string.Empty);
                }

                count++;
            }

            return result.ToString();
        }

        private static List<QuestionnaireAnswerItem> ParseQuestionnaire(
            string questionnaire,
            ITracingService tracingService)
        {
            /*
             * Input examples:
             *
             * qid$$$answer
             * qid$$$answer1$$$answer2
             * qid$$$
             * qid$$$value###qid2$$$value2
             */

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
                if (string.IsNullOrWhiteSpace(block))
                {
                    continue;
                }

                string[] parts = block.Split(
                    new string[] { AnswerSeparator },
                    StringSplitOptions.None
                );

                if (parts.Length < 2)
                {
                    tracingService.Trace("Skipping invalid questionnaire block: {0}", block);
                    continue;
                }

                Guid questionId;

                if (!Guid.TryParse(parts[0], out questionId))
                {
                    tracingService.Trace("Skipping block with invalid question ID: {0}", parts[0]);
                    continue;
                }

                for (int i = 1; i < parts.Length; i++)
                {
                    string answerValue = parts[i];

                    QuestionnaireAnswerItem item = new QuestionnaireAnswerItem
                    {
                        QuestionId = questionId,
                        AnswerValue = answerValue
                    };

                    result.Add(item);
                }
            }

            return result;
        }

        private static void CreateAnswerRecord(
            IOrganizationService service,
            Guid caseId,
            QuestionnaireAnswerItem item,
            ITracingService tracingService)
        {
            Entity answer = new Entity(AnswerEntityName);

            answer[AnswerCaseLookup] = new EntityReference(CaseEntityName, caseId);
            answer[AnswerQuestionLookup] = new EntityReference("crm2p_question", item.QuestionId);
            answer[AnswerValueField] = item.AnswerValue ?? string.Empty;

            Guid answerId = service.Create(answer);

            tracingService.Trace(
                "Created answer record {0}. Question: {1}, Value: {2}",
                answerId,
                item.QuestionId,
                item.AnswerValue
            );
        }

        private static string GetString(Entity entity, string attributeName)
        {
            if (entity == null || !entity.Contains(attributeName) || entity[attributeName] == null)
            {
                return string.Empty;
            }

            return entity[attributeName].ToString();
        }

        private static EntityReference GetEntityReference(Entity entity, string attributeName)
        {
            if (entity == null || !entity.Contains(attributeName) || entity[attributeName] == null)
            {
                return null;
            }

            return entity[attributeName] as EntityReference;
        }

        private static string GetAnswerValueAsString(Entity entity, string attributeName)
        {
            if (entity == null || !entity.Contains(attributeName) || entity[attributeName] == null)
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

        private class QuestionnaireAnswerItem
        {
            public Guid QuestionId { get; set; }
            public string AnswerValue { get; set; }
        }
    }
}
