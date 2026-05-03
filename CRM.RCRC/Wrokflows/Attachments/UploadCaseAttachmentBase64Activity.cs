using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Sdk.Workflow;
using RCRC.CRM.Plugins.Services;
using System;
using System.Activities;
using System.Collections.Generic;

namespace RCRC.CRM.WorkflowActivities.Attachments
{
    public class UploadCaseAttachmentBase64Activity : CodeActivity
    {
        private const string SystemConfigurationEntityName = "crm2p_systemconfiguration";
        private const string SystemConfigurationNameAttribute = "crm2p_name";
        private const string SystemConfigurationValueAttribute = "crm2p_value";
        private const string AttachmentApiBaseUrlConfigurationName = "ATTACHMENT_API_BASE_URL";
        private const string AttachmentApiUsernameConfigurationName = "ATTACHMENT_API_USERNAME";
        private const string AttachmentApiPasswordConfigurationName = "ATTACHMENT_API_PASSWORD";

        [RequiredArgument]
        [Input("Case")]
        [ReferenceTarget("incident")]
        public InArgument<EntityReference> Case { get; set; }

        [RequiredArgument]
        [Input("File Name")]
        public InArgument<string> FileName { get; set; }

        [RequiredArgument]
        [Input("Base64 File Content")]
        public InArgument<string> Base64FileContent { get; set; }

        [Input("File Description")]
        public InArgument<string> FileDescription { get; set; }

        [Input("Content Type")]
        public InArgument<string> ContentType { get; set; }

        [Output("Success")]
        public OutArgument<bool> Success { get; set; }

        [Output("Message")]
        public OutArgument<string> Message { get; set; }

        [Output("Attachment Ids")]
        public OutArgument<string> AttachmentIds { get; set; }

        protected override void Execute(CodeActivityContext executionContext)
        {
            var tracingService = executionContext.GetExtension<ITracingService>();
            var workflowContext = executionContext.GetExtension<IWorkflowContext>();
            var serviceFactory = executionContext.GetExtension<IOrganizationServiceFactory>();

            if (workflowContext == null)
                throw new InvalidPluginExecutionException("Workflow context is not available.");

            if (serviceFactory == null)
                throw new InvalidPluginExecutionException("Organization service factory is not available.");

            var service = serviceFactory.CreateOrganizationService(workflowContext.UserId);

            try
            {
                Trace(tracingService, "UploadCaseAttachmentBase64Activity started.");

                var caseReference = Case.Get(executionContext);
                var fileName = FileName.Get(executionContext);
                var base64FileContent = Base64FileContent.Get(executionContext);
                var fileDescription = FileDescription.Get(executionContext);
                var contentType = ContentType.Get(executionContext);

                if (caseReference == null || caseReference.Id == Guid.Empty)
                    throw new InvalidPluginExecutionException("Case is required and must contain a valid incident reference.");

                if (string.IsNullOrWhiteSpace(fileName))
                    throw new InvalidPluginExecutionException("File Name is required.");

                if (string.IsNullOrWhiteSpace(base64FileContent))
                    throw new InvalidPluginExecutionException("Base64 File Content is required.");

                if (string.IsNullOrWhiteSpace(contentType))
                    contentType = "application/octet-stream";

                Trace(tracingService, "Reading attachment API system configuration values.");

                var baseUrl = GetSystemConfigurationValue(service, AttachmentApiBaseUrlConfigurationName);
                var username = GetSystemConfigurationValue(service, AttachmentApiUsernameConfigurationName);
                var password = GetSystemConfigurationValue(service, AttachmentApiPasswordConfigurationName);

                Trace(tracingService, "Attachment API configuration loaded.");
                Trace(tracingService, "Uploading attachment for case ID: {0}", caseReference.Id);

                var tokenManager = new BearerTokenManager(baseUrl, username, password);
                var uploader = new CaseAttachmentUploader(baseUrl, tokenManager);
                var files = new List<UploadAttachmentFile>
                {
                    new UploadAttachmentFile
                    {
                        FileName = fileName,
                        Base64Content = base64FileContent,
                        FileDescription = fileDescription,
                        ContentType = contentType
                    }
                };

                var result = uploader
                    .UploadAttachmentComplainAsync(caseReference.Id, files)
                    .GetAwaiter()
                    .GetResult();

                if (result == null)
                    throw new InvalidPluginExecutionException("Attachment upload returned no result.");

                var attachmentIds = string.Empty;
                if (result.Data != null && result.Data.Count > 0)
                {
                    var idValues = new List<string>();
                    for (var i = 0; i < result.Data.Count; i++)
                    {
                        idValues.Add(result.Data[i].ToString());
                    }

                    attachmentIds = string.Join(",", idValues);
                }

                Success.Set(executionContext, result.Success);
                Message.Set(executionContext, result.Message ?? string.Empty);
                AttachmentIds.Set(executionContext, attachmentIds);

                Trace(
                    tracingService,
                    "Attachment upload completed. Success={0}. AttachmentIds={1}",
                    result.Success,
                    attachmentIds);

                if (!result.Success)
                    throw new InvalidPluginExecutionException(result.Message ?? "Attachment upload failed.");
            }
            catch (Exception ex)
            {
                Trace(tracingService, "UploadCaseAttachmentBase64Activity failed: {0}", ex.ToString());

                Success.Set(executionContext, false);
                Message.Set(executionContext, ex.Message ?? string.Empty);
                AttachmentIds.Set(executionContext, string.Empty);

                if (ex is InvalidPluginExecutionException)
                    throw;

                throw new InvalidPluginExecutionException(ex.Message, ex);
            }
        }

        private static string GetSystemConfigurationValue(IOrganizationService service, string name)
        {
            var query = new QueryExpression(SystemConfigurationEntityName)
            {
                ColumnSet = new ColumnSet(SystemConfigurationValueAttribute),
                TopCount = 1
            };

            query.Criteria.AddCondition(SystemConfigurationNameAttribute, ConditionOperator.Equal, name);

            var results = service.RetrieveMultiple(query);
            var record = results != null && results.Entities.Count > 0
                ? results.Entities[0]
                : null;

            if (record == null)
            {
                throw new InvalidPluginExecutionException(
                    "Missing system configuration record: " + name);
            }

            var value = record.GetAttributeValue<string>(SystemConfigurationValueAttribute);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidPluginExecutionException(
                    "System configuration value is empty: " + name);
            }

            return value;
        }

        private static void Trace(ITracingService tracingService, string format, params object[] args)
        {
            if (tracingService == null)
                return;

            tracingService.Trace(format, args);
        }
    }
}
