using Jtext103.CFET2.Core;
using Jtext103.CFET2.Core.Attributes;
using Jtext103.CFET2.Core.Sample;
using Jtext103.CFET2.HTTPCommunicationModule;
using MessagePack;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TestCode;

[TestClass]
public sealed partial class HTTPCommunicationModuleTests : CFET2Host
{
    private HTTPCommunicationModule? module;
    private HttpClient? client;
    private string baseUrl = "";
    private string? webUiRoot;
    private string? previousWebUiRoot;
    private static readonly DateTimeOffset SourceTime = new DateTimeOffset(2026, 9, 24, 12, 30, 0, TimeSpan.FromHours(8)).AddTicks(1234567);

    [TestInitialize]
    public void Init()
    {
        KillHubIfAlive();
        HubMaster.InjectHubToModule(this);

        webUiRoot = CreateWebUiRoot();
        previousWebUiRoot = Environment.GetEnvironmentVariable("CFET2_WEB_UI_ROOT");
        Environment.SetEnvironmentVariable("CFET2_WEB_UI_ROOT", webUiRoot);

        baseUrl = $"http://127.0.0.1:{GetFreePort()}";
        module = new HTTPCommunicationModule(new Uri(baseUrl));

        MyHub.TryAddCommunicationModule(module);
        MyHub.TryAddThing(new HttpTestThing(), "/", "thing");
        MyHub.StartCommunication();

        client = new HttpClient();
    }

    [TestCleanup]
    public void Cleanup()
    {
        client?.Dispose();
        module?.Dispose();
        Environment.SetEnvironmentVariable("CFET2_WEB_UI_ROOT", previousWebUiRoot);

        if (webUiRoot != null && Directory.Exists(webUiRoot))
        {
            Directory.Delete(webUiRoot, recursive: true);
        }

        KillHubIfAlive();
    }

    [TestMethod]
    public void ProtocolNames_ShouldRegisterHttp()
    {
        CollectionAssert.Contains(module!.ProtocolNames, "http");
        CollectionAssert.DoesNotContain(module.ProtocolNames, "https");
    }

    [TestMethod]
    [DataRow("JSON")]
    [DataRow("MessagePack")]
    public void Timestamp_ShouldSurviveRemoteSampleRoundTrip(string format)
    {
        using var reader = new HTTPCommunicationModule(new Uri(baseUrl), format);
        var sample = reader.TryGetResourceSampleWithUri($"{baseUrl}/thing/TimestampedValue", Array.Empty<object>());
        Assert.AreEqual(42, sample.GetVal<int>());
        Assert.AreEqual(SourceTime, sample.Timestamp);
        Assert.AreEqual(TimeSpan.Zero, sample.Timestamp!.Value.Offset);
        Assert.IsTrue(sample.IsRemote);

        var legacy = reader.TryGetResourceSampleWithUri($"{baseUrl}/thing/StatusValue", Array.Empty<object>());
        Assert.IsNull(legacy.Timestamp);
    }

    [TestMethod]
    public async Task GetRequest_ShouldReturnSampleContextJson()
    {
        var response = await client!.GetAsync($"{baseUrl}/thing/StatusValue");
        var json = await ReadJsonAsync(response);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(10, json.Value<int>(SampleBase<object>.KeyOfVal));
        Assert.IsTrue(json.Value<bool>(SampleBase<object>.KeyOfIsValid));
        Assert.IsTrue(json.Value<bool>(SampleBase<object>.KeyOfIsRemote));
        Assert.AreEqual("/thing/StatusValue", json.Value<string>(SampleBase<object>.KeyOfPath));
    }

    [TestMethod]
    public async Task PostRequest_WithArrayBody_ShouldSetConfig()
    {
        using var body = JsonBody("[7]");

        var setResponse = await client!.PostAsync($"{baseUrl}/thing/ConfigValue", body);
        var getJson = await ReadJsonAsync(await client.GetAsync($"{baseUrl}/thing/ConfigValue"));

        Assert.AreEqual(HttpStatusCode.OK, setResponse.StatusCode);
        Assert.AreEqual(7, getJson.Value<int>(SampleBase<object>.KeyOfVal));
    }

    [TestMethod]
    public async Task PutRequest_WithDictionaryEnvelope_ShouldInvokeMethod()
    {
        using var body = JsonBody("""
            {
              "usingInputDict": true,
              "inputDict": {
                "left": "A",
                "right": 3
              }
            }
            """);

        var response = await client!.PutAsync($"{baseUrl}/thing/Join", body);
        var json = await ReadJsonAsync(response);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("A:3", json.Value<string>(SampleBase<object>.KeyOfVal));
    }

    [TestMethod]
    public async Task PutRequest_WithPlainObjectBody_ShouldUseDictionaryFallback()
    {
        using var body = JsonBody("""
            {
              "left": "B",
              "right": 4
            }
            """);

        var response = await client!.PutAsync($"{baseUrl}/thing/Join", body);
        var json = await ReadJsonAsync(response);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("B:4", json.Value<string>(SampleBase<object>.KeyOfVal));
    }

    [TestMethod]
    public async Task InvalidJsonBody_CurrentlyReturnsInternalServerError()
    {
        using var body = JsonBody("{ not valid json");

        var response = await client!.PostAsync($"{baseUrl}/thing/ConfigValue", body);

        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [TestMethod]
    public async Task MissingResource_ShouldReturnNotFound()
    {
        var response = await client!.GetAsync($"{baseUrl}/thing/MissingStatus");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        AssertCorsHeaders(response);
    }

    [TestMethod]
    public async Task UnsupportedMethod_ShouldReturnMethodNotAllowed()
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{baseUrl}/thing/StatusValue");

        var response = await client!.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        AssertCorsHeaders(response);
    }

    [TestMethod]
    public async Task OptionsRequest_ShouldReturnCorsPreflightHeaders()
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, $"{baseUrl}/thing/StatusValue");

        var response = await client!.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        AssertCorsHeaders(response);
        StringAssert.Contains(response.Headers.GetValues("Access-Control-Allow-Methods").Single(), "GET");
    }

    [TestMethod]
    public async Task BrowserHtmlRequest_ShouldRedirectToWebUiHash()
    {
        using var noRedirectClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/thing/StatusValue?x=1");
        request.Headers.Accept.ParseAdd("text/html");

        var response = await noRedirectClient.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
        Assert.AreEqual("/views/index.html#/thing/StatusValue?x=1", response.Headers.Location?.ToString());
        AssertCorsHeaders(response);
    }

    [TestMethod]
    public async Task WebUiIndex_ShouldBeServedFromConfiguredRoot()
    {
        var response = await client!.GetAsync($"{baseUrl}/views/index.html");
        var html = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("text/html", response.Content.Headers.ContentType?.MediaType);
        StringAssert.Contains(html, "<div id=\"app\"></div>");
    }

    [TestMethod]
    public async Task MessagePackRequest_ShouldReturnMessagePackSampleContext()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/thing/StatusValue");
        request.Headers.Accept.ParseAdd("application/MessagePack");
        request.Headers.AcceptEncoding.ParseAdd("MessagePack");

        var response = await client!.SendAsync(request);
        var context = MessagePackSerializer.Deserialize<Dictionary<string, object>>(
            await response.Content.ReadAsStreamAsync(),
            MessagePack.Resolvers.ContractlessStandardResolver.Options);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/MessagePack", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("MessagePack", response.Content.Headers.ContentEncoding.Single());
        Assert.AreEqual(10, Convert.ToInt32(context[SampleBase<object>.KeyOfVal]));
        Assert.IsTrue((bool)context[SampleBase<object>.KeyOfIsRemote]);
    }

    [TestMethod]
    public void HubRemoteGet_ShouldUseHttpClientPath()
    {
        var sample = MyHub.Get($"{baseUrl}/thing/StatusValue");

        Assert.AreEqual(10, sample.GetVal<int>());
        Assert.IsTrue(sample.IsRemote);
        Assert.AreEqual("/thing/StatusValue", sample.Path);
    }

    [TestMethod]
    public void HubRemoteSet_WithArrayInput_ShouldSendPostBody()
    {
        var request = new ResourceRequest(
            $"{baseUrl}/thing/ConfigValue",
            AccessAction.set,
            [12],
            null,
            null);

        var setSample = MyHub.TryAccessResourceSampleWithUri(request);
        var getSample = MyHub.Get($"{baseUrl}/thing/ConfigValue");

        Assert.IsTrue(setSample.IsRemote);
        Assert.AreEqual(12, getSample.GetVal<int>());
    }

    [TestMethod]
    public void HubRemoteInvoke_WithDictionaryInput_ShouldSendPutBody()
    {
        var request = new ResourceRequest(
            $"{baseUrl}/thing/Join",
            AccessAction.invoke,
            null,
            new Dictionary<string, object>
            {
                ["left"] = "C",
                ["right"] = 5
            },
            null,
            usingdict: true);

        var sample = MyHub.TryAccessResourceSampleWithUri(request);

        Assert.IsTrue(sample.IsRemote);
        Assert.AreEqual("C:5", sample.GetVal<string>());
    }

    private static StringContent JsonBody(string json)
    {
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private static async Task<JObject> ReadJsonAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        return JObject.Parse(content);
    }

    private static void AssertCorsHeaders(HttpResponseMessage response)
    {
        Assert.AreEqual("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string CreateWebUiRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "cfet2-httpcm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "index.html"), "<!doctype html><div id=\"app\"></div>");
        File.WriteAllText(Path.Combine(root, "WindowsAppIndex.html"), "<!doctype html><div id=\"app\"></div>");
        return root;
    }

    private static void KillHubIfAlive()
    {
        try
        {
            Hub.KillMaster();
        }
        catch
        {
        }
    }

    private sealed class HttpTestThing : Thing
    {
        [Cfet2Status]
        public Status<int> TimestampedValue => new Status<int>(42) { Timestamp = SourceTime };

        [Cfet2Status]
        public int StatusValue => 10;

        [Cfet2Config]
        public int ConfigValue { get; set; } = 1;

        [Cfet2Method]
        public string Join(string left, int right)
        {
            return $"{left}:{right}";
        }
    }
}
