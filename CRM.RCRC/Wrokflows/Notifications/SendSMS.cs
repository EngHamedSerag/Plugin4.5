using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Sdk.Workflow;
using System;
using System.Activities;
using System.Net;
using System.Net.Http;
using System.Text;

namespace RCRC.CRM.WorkflowActivities.SMS
{
    public class SendSmsActivity : CodeActivity
    {
        private const string SystemConfigurationEntityName = "crm2p_systemconfiguration";
        private const string SystemConfigurationNameAttribute = "crm2p_name";
        private const string SystemConfigurationValueAttribute = "crm2p_value";

        private const string SmsGatewayEndpointConfigurationName = "SMS_GATEWAY_ENDPOINT";
        private const string SmsGatewayApiKeyConfigurationName = "SMS_GATEWAY_X_API_KEY";

        [RequiredArgument]
        [Input("Mobile Number")]
        public InArgument<string> MobileNumber { get; set; }

        [RequiredArgument]
        [Input("SMS Text")]
        public InArgument<string> SmsText { get; set; }

        [Output("Success")]
        public OutArgument<bool> Success { get; set; }

        [Output("Message")]
        public OutArgument<string> Message { get; set; }

        [Output("Status Code")]
        public OutArgument<int> StatusCode { get; set; }

        [Output("Response Body")]
        public OutArgument<string> ResponseBody { get; set; }

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
                Trace(tracingService, "SendSmsActivity started.");

                var mobileNumber = MobileNumber.Get(executionContext);
                var smsText = SmsText.Get(executionContext);

                if (string.IsNullOrWhiteSpace(mobileNumber))
                    throw new InvalidPluginExecutionException("Mobile Number is required.");

                if (string.IsNullOrWhiteSpace(smsText))
                    throw new InvalidPluginExecutionException("SMS Text is required.");

                mobileNumber = mobileNumber.Trim();
                smsText = smsText.Trim();

                Trace(tracingService, "Reading SMS gateway system configuration values.");

                var endpoint = GetSystemConfigurationValue(service, SmsGatewayEndpointConfigurationName);
                var apiKey = GetSystemConfigurationValue(service, SmsGatewayApiKeyConfigurationName);

                Trace(tracingService, "SMS gateway configuration loaded.");
                Trace(tracingService, "Sending SMS to mobile number: {0}", MaskMobileNumber(mobileNumber));

                var result = SendSms(endpoint, apiKey, mobileNumber, smsText, tracingService);

                Success.Set(executionContext, result.Success);
                Message.Set(executionContext, result.Message ?? string.Empty);
                StatusCode.Set(executionContext, result.StatusCode);
                ResponseBody.Set(executionContext, result.ResponseBody ?? string.Empty);

                Trace(
                    tracingService,
                    "SMS sending completed. Success={0}, StatusCode={1}, Message={2}",
                    result.Success,
                    result.StatusCode,
                    result.Message);

                if (!result.Success)
                {
                    throw new InvalidPluginExecutionException(
                        "SMS sending failed. Status code: " +
                        result.StatusCode +
                        ". Response body: " +
                        (result.ResponseBody ?? string.Empty));
                }
            }
            catch (Exception ex)
            {
                Trace(tracingService, "SendSmsActivity failed: {0}", ex.ToString());

                Success.Set(executionContext, false);
                Message.Set(executionContext, ex.Message ?? string.Empty);
                StatusCode.Set(executionContext, 0);
                ResponseBody.Set(executionContext, string.Empty);

                if (ex is InvalidPluginExecutionException)
                    throw;

                throw new InvalidPluginExecutionException(ex.Message, ex);
            }
        }

        private static SmsSendResult SendSms(
            string endpoint,
            string apiKey,
            string mobileNumber,
            string smsText,
            ITracingService tracingService)
        {
            EnsureTls12();

            var requestBody =
                "{" +
                "\"number\":\"" + EscapeJsonString(mobileNumber) + "\"," +
                "\"text\":\"" + EscapeJsonString(smsText) + "\"" +
                "}";

            Trace(tracingService, "SMS request body prepared.");

            using (var client = new HttpClient())
            using (var content = new StringContent(requestBody, Encoding.UTF8, "application/json"))
            {
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(
                    new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

                var response = client
                    .PostAsync(endpoint, content)
                    .GetAwaiter()
                    .GetResult();

                var responseBody = response.Content == null
                    ? string.Empty
                    : response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                var statusCode = (int)response.StatusCode;
                var success = response.IsSuccessStatusCode;

                return new SmsSendResult
                {
                    Success = success,
                    StatusCode = statusCode,
                    ResponseBody = responseBody,
                    Message = success
                        ? "SMS sent successfully."
                        : "SMS sending failed."
                };
            }
        }

        private static string GetSystemConfigurationValue(IOrganizationService service, string name)
        {
            var query = new QueryExpression(SystemConfigurationEntityName)
            {
                ColumnSet = new ColumnSet(SystemConfigurationValueAttribute),
                TopCount = 1
            };

            query.Criteria.AddCondition(
                SystemConfigurationNameAttribute,
                ConditionOperator.Equal,
                name);

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

            return value.Trim();
        }

        private static void EnsureTls12()
        {
            ServicePointManager.SecurityProtocol =
                ServicePointManager.SecurityProtocol | (SecurityProtocolType)3072;
        }

        private static string EscapeJsonString(string value)
        {
            if (value == null)
                return string.Empty;

            var builder = new StringBuilder();

            foreach (var c in value)
            {
                switch (c)
                {
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (c < 32)
                        {
                            builder.Append("\\u");
                            builder.Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            builder.Append(c);
                        }
                        break;
                }
            }

            return builder.ToString();
        }

        private static string MaskMobileNumber(string mobileNumber)
        {
            if (string.IsNullOrWhiteSpace(mobileNumber))
                return string.Empty;

            if (mobileNumber.Length <= 4)
                return "****";

            return new string('*', mobileNumber.Length - 4) +
                   mobileNumber.Substring(mobileNumber.Length - 4);
        }

        private static void Trace(ITracingService tracingService, string format, params object[] args)
        {
            if (tracingService == null)
                return;

            tracingService.Trace(format, args);
        }

        private class SmsSendResult
        {
            public bool Success { get; set; }
            public string Message { get; set; }
            public int StatusCode { get; set; }
            public string ResponseBody { get; set; }
        }
    }
}
