using Jtext103.CFET2.Core.Sample;
using MessagePack;
using Newtonsoft.Json;
using System.Net.Http.Headers;
using System.Text;

namespace Jtext103.CFET2.HTTPCommunicationModule
{
    public partial class HTTPCommunicationModule
    {
        private static readonly HttpClient _client = new HttpClient();

        public override ISample TryGetResourceSampleWithUri(string requestUri, Dictionary<string, object> inputDict)
        {
            return HTTPRequestSample("GET", requestUri, null, inputDict, true);
        }

        public override ISample TryGetResourceSampleWithUri(string requestUri, params object[] inputs)
        {
            return HTTPRequestSample("GET", requestUri, inputs, null, false);
        }

        public override ISample TryInvokeSampleResourceWithUri(string requestUri, Dictionary<string, object> inputDict)
        {
            return HTTPRequestSample("PUT", requestUri, null, inputDict, true);
        }

        public override ISample TryInvokeSampleResourceWithUri(string requestUri, params object[] inputs)
        {
            return HTTPRequestSample("PUT", requestUri, inputs, null, false);
        }

        public override ISample TrySetResourceSampleWithUri(string requestUri, Dictionary<string, object> inputDict)
        {
            return HTTPRequestSample("POST", requestUri, null, inputDict, true);
        }

        public override ISample TrySetResourceSampleWithUri(string requestUri, params object[] inputs)
        {
            return HTTPRequestSample("POST", requestUri, inputs, null, false);
        }

        private ISample HTTPRequestSample(string method, string requestUri, object[]? inputArray,
            Dictionary<string, object>? inputDict, bool usingInputDict)
        {
            bool useMessagePack = accept.Equals("MessagePack", StringComparison.InvariantCultureIgnoreCase);
            return ModernHTTPRequestAsync(method, requestUri, inputArray, inputDict, usingInputDict, useMessagePack)
                .GetAwaiter().GetResult();
        }

        private async Task<ISample> ModernHTTPRequestAsync(string method, string requestUri, object[]? inputArray,
            Dictionary<string, object>? inputDict, bool usingInputDict, bool useMessagePack = false)
        {
            var request = new HttpRequestMessage(new HttpMethod(method), requestUri);

            if (HasExplicitInput(inputArray, inputDict, usingInputDict))
            {
                var envelope = new
                {
                    usingInputDict,
                    inputArray = usingInputDict ? null : inputArray,
                    inputDict = usingInputDict ? inputDict : null
                };
                request.Content = new StringContent(JsonConvert.SerializeObject(envelope), Encoding.UTF8, "application/json");
            }

            if (useMessagePack)
            {
                request.Headers.AcceptEncoding.ParseAdd("MessagePack");
                request.Headers.Accept.ParseAdd("application/MessagePack");
            }
            else
            {
                request.Headers.Accept.ParseAdd("application/JSON");
            }

            HttpResponseMessage response;
            try
            {
                response = await _client.SendAsync(request);
            }
            catch (Exception ex)
            {
                throw new Exception($"HTTP {method} {requestUri} failed: {ex}");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"HTTP {method} {requestUri} failed with status {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            ISample result = new SampleBase<object>();
            Dictionary<string, object>? parsedDict;

            if (IsMessagePackResponse(response.Content.Headers))
            {
                using var stream = await response.Content.ReadAsStreamAsync();
                parsedDict = MessagePackSerializer.Deserialize<Dictionary<string, object>>(
                    stream,
                    MessagePack.Resolvers.ContractlessStandardResolver.Options);
            }
            else
            {
                var contentString = await response.Content.ReadAsStringAsync();
                parsedDict = JsonConvert.DeserializeObject<Dictionary<string, object>>(contentString);
            }

            if (parsedDict != null)
            {
                result.Context = new Dictionary<string, object>(parsedDict, StringComparer.InvariantCultureIgnoreCase);
            }

            result.IsRemote = true;
            return result;
        }

        private static bool HasExplicitInput(object[]? inputArray, Dictionary<string, object>? inputDict, bool usingInputDict)
        {
            if (usingInputDict)
            {
                return inputDict != null && inputDict.Count > 0;
            }

            return inputArray != null && inputArray.Length > 0;
        }

        private static bool IsMessagePackResponse(HttpContentHeaders headers)
        {
            var mediaType = headers.ContentType?.MediaType ?? "";
            if (mediaType.Contains("MessagePack", StringComparison.InvariantCultureIgnoreCase))
            {
                return true;
            }

            return headers.ContentEncoding.Any(i => i.Contains("MessagePack", StringComparison.InvariantCultureIgnoreCase));
        }
    }
}
