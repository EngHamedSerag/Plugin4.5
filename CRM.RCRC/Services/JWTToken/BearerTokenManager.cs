using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RCRC.CRM.Plugins.Services
{
    public class BearerTokenManager
    {
        private static readonly string[] TokenPaths = new[]
        {
            "token",
            "Token",
            "JWToken",
            "access_token",
            "AccessToken",
            "Data.token",
            "Data.Token",
            "Data.JWToken",
            "Data.access_token",
            "Data.AccessToken",
            "data.token",
            "data.Token",
            "data.JWToken",
            "data.access_token",
            "data.AccessToken"
        };

        private static readonly string[] ExpiryPaths = new[]
        {
            "expires_in",
            "ExpiresIn",
            "expiresIn",
            "Data.expires_in",
            "Data.ExpiresIn",
            "Data.expiresIn",
            "data.expires_in",
            "data.ExpiresIn",
            "data.expiresIn",
            "expires_on",
            "ExpiresOn",
            "expiresOn",
            "Data.expires_on",
            "Data.ExpiresOn",
            "Data.expiresOn",
            "data.expires_on",
            "data.ExpiresOn",
            "data.expiresOn"
        };

        private readonly string _baseUrl;
        private readonly string _username;
        private readonly string _password;
        private readonly SemaphoreSlim _tokenSemaphore = new SemaphoreSlim(1, 1);

        private string _token;
        private DateTime _tokenExpiresOnUtc = DateTime.MinValue;

        public BearerTokenManager(string baseUrl, string username, string password)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new ArgumentException("Base URL is required.", nameof(baseUrl));

            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException("Username is required.", nameof(username));

            if (string.IsNullOrWhiteSpace(password))
                throw new ArgumentException("Password is required.", nameof(password));

            _baseUrl = baseUrl.TrimEnd('/');
            _username = username;
            _password = password;
        }

        public async Task<string> GetTokenAsync()
        {
            if (HasValidToken())
                return _token;

            await _tokenSemaphore.WaitAsync().ConfigureAwait(false);

            try
            {
                if (HasValidToken())
                    return _token;

                return await LoginAsync().ConfigureAwait(false);
            }
            finally
            {
                _tokenSemaphore.Release();
            }
        }

        private bool HasValidToken()
        {
            return !string.IsNullOrWhiteSpace(_token)
                && DateTime.UtcNow < _tokenExpiresOnUtc.AddMinutes(-2);
        }

        private async Task<string> LoginAsync()
        {
            EnsureTls12();

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));

                var requestBody = new
                {
                    UserName = _username,
                    Password = _password
                };

                var content = new StringContent(
                    JsonConvert.SerializeObject(requestBody),
                    Encoding.UTF8,
                    "application/json");

                var loginUrl = _baseUrl + "/api/Accounts/Login";
                HttpResponseMessage response;

                try
                {
                    response = await client.PostAsync(loginUrl, content).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    throw new InvalidOperationException(
                        "Could not connect to attachment API login endpoint: " + loginUrl
                        + ". Verify ATTACHMENT_API_BASE_URL, protocol/port, firewall rules, and that the attachment API is reachable from the CRM server.",
                        ex);
                }
                catch (TaskCanceledException ex)
                {
                    throw new InvalidOperationException(
                        "Attachment API login request timed out: " + loginUrl
                        + ". Verify the attachment API is reachable from the CRM server.",
                        ex);
                }

                var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        string.Format(
                            "Login request failed. Status code: {0} ({1}). Response body: {2}",
                            (int)response.StatusCode,
                            response.ReasonPhrase,
                            responseBody));
                }

                var responseObject = ParseJsonObject(responseBody, "Login response");
                _token = ExtractToken(responseObject, responseBody);
                _tokenExpiresOnUtc = ExtractExpiryUtc(responseObject);

                return _token;
            }
        }

        private static JObject ParseJsonObject(string json, string context)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidOperationException(context + " was empty.");

            try
            {
                return JObject.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    context + " was not valid JSON. Body: " + json,
                    ex);
            }
        }

        private static string ExtractToken(JObject responseObject, string responseBody)
        {
            for (var i = 0; i < TokenPaths.Length; i++)
            {
                var tokenNode = responseObject.SelectToken(TokenPaths[i]);
                if (tokenNode == null || tokenNode.Type == JTokenType.Null)
                    continue;

                var token = tokenNode.Type == JTokenType.String
                    ? tokenNode.Value<string>()
                    : tokenNode.ToString(Formatting.None);

                if (!string.IsNullOrWhiteSpace(token))
                    return token;
            }

            throw new InvalidOperationException(
                "Login succeeded but no bearer token was found in the response. Body: " + responseBody);
        }

        private static DateTime ExtractExpiryUtc(JObject responseObject)
        {
            for (var i = 0; i < ExpiryPaths.Length; i++)
            {
                var expiryNode = responseObject.SelectToken(ExpiryPaths[i]);
                if (expiryNode == null || expiryNode.Type == JTokenType.Null)
                    continue;

                DateTime expiryUtc;
                if (TryParseExpiry(expiryNode, out expiryUtc))
                    return expiryUtc;
            }

            return DateTime.UtcNow.AddHours(1);
        }

        private static bool TryParseExpiry(JToken expiryNode, out DateTime expiryUtc)
        {
            expiryUtc = DateTime.MinValue;

            if (expiryNode.Type == JTokenType.Integer || expiryNode.Type == JTokenType.Float)
            {
                double seconds;
                if (double.TryParse(
                    expiryNode.ToString(),
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out seconds) && seconds > 0)
                {
                    expiryUtc = DateTime.UtcNow.AddSeconds(seconds);
                    return true;
                }
            }

            var stringValue = expiryNode.ToString();
            if (string.IsNullOrWhiteSpace(stringValue))
                return false;

            long unixSeconds;
            if (long.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out unixSeconds)
                && unixSeconds > 0)
            {
                if (unixSeconds >= 1000000000)
                {
                    expiryUtc = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
                    return true;
                }

                expiryUtc = DateTime.UtcNow.AddSeconds(unixSeconds);
                return true;
            }

            DateTime parsedDate;
            if (DateTime.TryParse(
                stringValue,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out parsedDate))
            {
                expiryUtc = parsedDate.ToUniversalTime();
                return true;
            }

            return false;
        }

        private static void EnsureTls12()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }
    }
}
