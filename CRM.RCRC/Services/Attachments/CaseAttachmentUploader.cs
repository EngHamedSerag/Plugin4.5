using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace RCRC.CRM.Plugins.Services
{
    public class CaseAttachmentUploader
    {
        private readonly string _baseUrl;
        private readonly BearerTokenManager _tokenManager;

        public CaseAttachmentUploader(string baseUrl, BearerTokenManager tokenManager)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new ArgumentException("Base URL is required.", nameof(baseUrl));

            if (tokenManager == null)
                throw new ArgumentNullException(nameof(tokenManager));

            _baseUrl = baseUrl.TrimEnd('/');
            _tokenManager = tokenManager;
        }
        public async Task<EndpointResponse<List<Guid>>> UploadAttachmentComplainAsync(
    int entityoption,
    string recordid,
    List<UploadAttachmentFile> files)
        {
            if (files == null || files.Count == 0)
                throw new ArgumentException("At least one file is required.", nameof(files));

            if (string.IsNullOrWhiteSpace(recordid))
                throw new ArgumentException("Record ID is required.", nameof(recordid));

            EnsureTls12();

            var token = await _tokenManager.GetTokenAsync().ConfigureAwait(false);

            var requestUrl =
                _baseUrl.TrimEnd('/') +
                "/api/Attachment/AddAttachmentComplain?recordID=" +
                Uri.EscapeDataString(recordid) +
                "&attachmentRelatedEntity=" +
                Uri.EscapeDataString(entityoption.ToString());

            using (var client = new HttpClient())
            using (var form = new MultipartFormDataContent())
            {
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));

                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);

                var descriptions = new List<string>();

                for (var i = 0; i < files.Count; i++)
                {
                    var file = files[i];

                    if (file == null)
                        throw new ArgumentException("File entry at index " + i + " is null.", nameof(files));

                    if (string.IsNullOrWhiteSpace(file.FileName))
                    {
                        throw new ArgumentException(
                            "FileName is required for file at index " + i + ".",
                            nameof(files));
                    }

                    if (string.IsNullOrWhiteSpace(file.Base64Content))
                    {
                        throw new ArgumentException(
                            "Base64Content is required for file '" + file.FileName + "'.",
                            nameof(files));
                    }

                    if (file.attachmentType <= 0)
                    {
                        throw new ArgumentException(
                            "Attachment type is required for file '" + file.FileName + "'.",
                            nameof(files));
                    }

                    var fileBytes = ConvertBase64ToBytes(file.Base64Content, file.FileName);
                    var fileContent = new ByteArrayContent(fileBytes);

                    var contentType = string.IsNullOrWhiteSpace(file.ContentType)
                        ? "application/octet-stream"
                        : file.ContentType.Trim();

                    fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);

                    /*
                     * Keep this as file0, file1, file2 because your current API most likely loops
                     * through httpRequest.Files by index.
                     */
                    form.Add(fileContent, "file" + i, file.FileName);

                    /*
                     * Required by API:
                     * httpRequest.Form[$"attachments[{fileIndex}].attachmentType"]
                     */
                    form.Add(
                        new StringContent(file.attachmentType.ToString()),
                        "attachments[" + i + "].attachmentType");

                    /*
                     * Optional but useful if the API reads per-file descriptions.
                     */
                    form.Add(
                        new StringContent(file.FileDescription ?? string.Empty),
                        "attachments[" + i + "].fileDescription");

                    descriptions.Add(file.FileDescription ?? string.Empty);
                }

                /*
                 * Keep this because your previous code was already sending FileDescription.
                 * Some APIs expect this general field instead of per-file description.
                 */
                form.Add(
                    new StringContent(string.Join(",", descriptions)),
                    "FileDescription");

                HttpResponseMessage response;

                try
                {
                    response = await client.PostAsync(requestUrl, form).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    throw new InvalidOperationException(
                        "Could not connect to attachment upload endpoint: " + requestUrl
                        + ". Verify ATTACHMENT_API_BASE_URL, protocol/port, firewall rules, and that the attachment API is reachable from the CRM server.",
                        ex);
                }
                catch (TaskCanceledException ex)
                {
                    throw new InvalidOperationException(
                        "Attachment upload request timed out: " + requestUrl
                        + ". Verify the attachment API is reachable from the CRM server.",
                        ex);
                }

                var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        string.Format(
                            "Attachment upload failed. Status code: {0} ({1}). URL: {2}. Response body: {3}",
                            (int)response.StatusCode,
                            response.ReasonPhrase,
                            requestUrl,
                            responseBody));
                }

                var result = JsonConvert.DeserializeObject<EndpointResponse<List<Guid>>>(responseBody);

                if (result == null)
                {
                    throw new InvalidOperationException(
                        "Attachment upload succeeded but the response body could not be deserialized. Body: " + responseBody);
                }

                return result;
            }
        }

        public async Task<EndpointResponse<byte[]>> GetFileContentAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path is required.", nameof(filePath));

            EnsureTls12();

            var requestUrl = _baseUrl
                + "/api/Attachment/GetFileContent?filePath="
                + Uri.EscapeDataString(filePath);

            return await GetByteArrayResponseAsync(requestUrl).ConfigureAwait(false);
        }

        private static byte[] ConvertBase64ToBytes(string base64Content, string fileName)
        {
            var normalizedBase64 = NormalizeBase64(base64Content);

            try
            {
                return Convert.FromBase64String(normalizedBase64);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(
                    "Base64 content is invalid for file '" + fileName + "'.",
                    ex);
            }
        }

        private static string NormalizeBase64(string base64Content)
        {
            var normalized = base64Content.Trim();

            if (normalized.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var commaIndex = normalized.IndexOf(',');
                if (commaIndex < 0 || commaIndex == normalized.Length - 1)
                {
                    throw new InvalidOperationException(
                        "Base64 content uses an invalid data URI format.");
                }

                normalized = normalized.Substring(commaIndex + 1);
            }

            return RemoveWhitespace(normalized);
        }

        private static string RemoveWhitespace(string value)
        {
            var buffer = new char[value.Length];
            var index = 0;

            for (var i = 0; i < value.Length; i++)
            {
                if (!char.IsWhiteSpace(value[i]))
                {
                    buffer[index] = value[i];
                    index++;
                }
            }

            return new string(buffer, 0, index);
        }

        private static void EnsureTls12()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        private async Task<EndpointResponse<byte[]>> GetByteArrayResponseAsync(string url)
        {
            using (var client = await CreateClientAsync().ConfigureAwait(false))
            {
                HttpResponseMessage response;

                try
                {
                    response = await client.GetAsync(url).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    throw new InvalidOperationException(
                        "Could not connect to attachment file content endpoint: " + url
                        + ". Verify ATTACHMENT_API_BASE_URL, protocol/port, firewall rules, and that the attachment API is reachable from the CRM server.",
                        ex);
                }
                catch (TaskCanceledException ex)
                {
                    throw new InvalidOperationException(
                        "Attachment file content request timed out: " + url
                        + ". Verify the attachment API is reachable from the CRM server.",
                        ex);
                }

                var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        string.Format(
                            "Get file content failed. Status code: {0} ({1}). Response body: {2}",
                            (int)response.StatusCode,
                            response.ReasonPhrase,
                            responseBody));
                }

                return ParseByteArrayEndpointResponse(responseBody);
            }
        }

        private async Task<HttpClient> CreateClientAsync()
        {
            var token = await _tokenManager.GetTokenAsync().ConfigureAwait(false);
            var client = new HttpClient();

            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            return client;
        }

        private static EndpointResponse<byte[]> ParseByteArrayEndpointResponse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidOperationException("Get file content response body was empty.");

            JObject responseObject;

            try
            {
                responseObject = JObject.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "Get file content response was not valid JSON. Body: " + json,
                    ex);
            }

            var result = new EndpointResponse<byte[]>();
            var successToken = responseObject["Success"] ?? responseObject["success"];
            var messageToken = responseObject["Message"] ?? responseObject["message"];
            var dataToken = responseObject["Data"] ?? responseObject["data"];

            result.Success = successToken != null && successToken.Type != JTokenType.Null
                ? successToken.Value<bool>()
                : false;
            result.Message = messageToken != null && messageToken.Type != JTokenType.Null
                ? messageToken.Value<string>()
                : string.Empty;

            if (dataToken == null || dataToken.Type == JTokenType.Null)
            {
                result.Data = null;
                return result;
            }

            if (dataToken.Type == JTokenType.String)
            {
                var base64Value = dataToken.Value<string>();
                result.Data = string.IsNullOrWhiteSpace(base64Value)
                    ? new byte[0]
                    : Convert.FromBase64String(base64Value);
                return result;
            }

            if (dataToken.Type == JTokenType.Array)
            {
                var bytes = new List<byte>();

                foreach (var item in dataToken)
                {
                    bytes.Add((byte)item.Value<int>());
                }

                result.Data = bytes.ToArray();
                return result;
            }

            try
            {
                result.Data = dataToken.ToObject<byte[]>();
                if (result.Data != null)
                    return result;
            }
            catch
            {
            }

            throw new InvalidOperationException(
                "Unsupported file content response Data format: " + dataToken.Type);
        }
    }

    public class EndpointResponse<T>
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public T Data { get; set; }
    }

    public class UploadAttachmentFile
    {
        public string FileName { get; set; }
        public string Base64Content { get; set; }
        public string FileDescription { get; set; }
        public string ContentType { get; set; }
        public int attachmentType { get; set; }
    }
}
