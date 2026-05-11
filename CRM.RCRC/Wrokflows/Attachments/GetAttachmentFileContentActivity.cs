using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Sdk.Workflow;
using CRM.Generic.Plugins.Services;
using System;
using System.Activities;

namespace CRM.Generic.WorkflowActivities.Attachments
{
    public class GetAttachmentFileContentActivity : CodeActivity
    {
        private const string SystemConfigurationEntityName = "crm2p_systemconfiguration";
        private const string SystemConfigurationNameAttribute = "crm2p_name";
        private const string SystemConfigurationValueAttribute = "crm2p_value";
        private const string ApiBaseUrlConfigurationName = "ATTACHMENT_API_BASE_URL";
        private const string ApiUsernameConfigurationName = "ATTACHMENT_API_USERNAME";
        private const string ApiPasswordConfigurationName = "ATTACHMENT_API_PASSWORD";

        [RequiredArgument]
        [Input("File Path")]
        public InArgument<string> FilePath { get; set; }

        [Output("Success")]
        public OutArgument<bool> Success { get; set; }

        [Output("Message")]
        public OutArgument<string> Message { get; set; }

        [Output("Base64 File Content")]
        public OutArgument<string> Base64FileContent { get; set; }

        protected override void Execute(CodeActivityContext executionContext)
        {
            var tracingService = executionContext.GetExtension<ITracingService>();
            var workflowContext = executionContext.GetExtension<IWorkflowContext>();
            var serviceFactory = executionContext.GetExtension<IOrganizationServiceFactory>();

            if (workflowContext == null)
                throw new InvalidPluginExecutionException("Workflow context is not available.");

            if (serviceFactory == null)
                throw new InvalidPluginExecutionException("Organization service factory is not available.");

            var organizationService = serviceFactory.CreateOrganizationService(workflowContext.UserId);

            try
            {
                Trace(tracingService, "GetAttachmentFileContentActivity started.");

                var filePath = FilePath.Get(executionContext);
                if (string.IsNullOrWhiteSpace(filePath))
                    throw new InvalidPluginExecutionException("File Path is required.");

                Trace(tracingService, "Reading attachment API configuration.");

                var baseUrl = GetSystemConfigurationValue(organizationService, ApiBaseUrlConfigurationName);
                var username = GetSystemConfigurationValue(organizationService, ApiUsernameConfigurationName);
                var password = GetSystemConfigurationValue(organizationService, ApiPasswordConfigurationName);

                Trace(tracingService, "Configuration loaded. Requesting file content for path: {0}", filePath);

                var tokenManager = new BearerTokenManager(baseUrl, username, password);
                var uploader = new CaseAttachmentUploader(baseUrl, tokenManager);
                var result = uploader.GetFileContentAsync(filePath).GetAwaiter().GetResult();

                if (result == null)
                    throw new InvalidPluginExecutionException("Get file content returned no result.");

                var base64Content = result.Data != null && result.Data.Length > 0
                    ? Convert.ToBase64String(result.Data)
                    : string.Empty;

                Success.Set(executionContext, result.Success);
                Message.Set(executionContext, result.Message ?? string.Empty);
                Base64FileContent.Set(executionContext, base64Content);

                Trace(
                    tracingService,
                    "Get file content completed. Success={0}. BytesLength={1}",
                    result.Success,
                    result.Data != null ? result.Data.Length : 0);

                if (!result.Success)
                    throw new InvalidPluginExecutionException(result.Message ?? "Get file content failed.");
            }
            catch (Exception ex)
            {
                Trace(tracingService, "GetAttachmentFileContentActivity failed: {0}", ex.ToString());

                Success.Set(executionContext, false);
                Message.Set(executionContext, ex.Message ?? string.Empty);
                Base64FileContent.Set(executionContext, string.Empty);

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
