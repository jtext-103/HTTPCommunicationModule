using Jtext103.CFET2.Core;
using Jtext103.CFET2.Core.Attributes;
using Jtext103.CFET2.Core.Communication;
using Jtext103.CFET2.Core.Event;
using Jtext103.CFET2.HTTPCommunicationModule;

namespace TestCode;

public sealed partial class HTTPCommunicationModuleTests
{
    [TestMethod, DataRow("JSON"), DataRow("MessagePack")]
    public void NoEventMetadataIsAvailableWithoutMiddlewareAndDoesNotChangeGet(string format)
    {
        var device = new QuietThing(); MyHub.TryAddThing(device, "/", "quiet");
        using var reader = new HTTPCommunicationModule(new Uri(baseUrl), format);
        var sample = reader.TryGetResourceSampleWithUri(baseUrl + "/quiet/Value", Array.Empty<object>());
        Assert.AreEqual(8, sample.GetVal<int>());
        Assert.IsTrue(Convert.ToBoolean(sample.Context["NoEvent"]));
        Assert.AreEqual(1, device.Reads);
        var normal = reader.TryGetResourceSampleWithUri(baseUrl + "/thing/StatusValue", Array.Empty<object>());
        Assert.IsFalse(Convert.ToBoolean(normal.Context["NoEvent"]));
    }

    [TestMethod]
    public void NoEventMetadataUsesTheRequestedResourceBeforeMiddlewareRewritesPaths()
    {
        var device = new QuietThing(); MyHub.TryAddThing(device, "/", "quiet");
        MyHub.Pipeline.AddMiddleware(new RewritePaths());
        using var reader = new HTTPCommunicationModule(new Uri(baseUrl));
        var sample = reader.TryGetResourceSampleWithUri(baseUrl + "/quiet/Value", Array.Empty<object>());
        Assert.AreEqual(8, sample.GetVal<int>());
        Assert.AreEqual("/thing/StatusValue", sample.Path);
        Assert.IsTrue(Convert.ToBoolean(sample.Context["NoEvent"]));
        Assert.AreEqual(1, device.Reads);
    }

    private sealed class RewritePaths : Jtext103.CFET2.Core.Middleware.CfetMiddlewareBase
    {
        public override Jtext103.CFET2.Core.Sample.ISample Process(Jtext103.CFET2.Core.Sample.ISample input, ResourceRequest request)
        {
            request.RequestUri = "/thing/StatusValue";
            input.Context["NoEvent"] = false;
            return input.SetPath("/thing/StatusValue");
        }
    }

    [TestMethod]
    public void RemoteNoEventSubscribeReadsOnceAndNeverCallsEventModule()
    {
        var device = new QuietThing(); MyHub.TryAddThing(device, "/", "quiet");
        var events = new RejectEventRegistration(); MyHub.TryAddCommunicationModule(events);
        using var sub = MyHub.SubscribeResource(baseUrl + "/quiet/Value", _ => Assert.Fail());
        Assert.AreEqual(ResourceSubscriptionStatus.Skipped, sub.Status);
        Assert.IsNull(sub.InitialValue); Assert.IsTrue(sub.Token.IsInactive);
        Assert.AreEqual(1, device.Reads); Assert.AreEqual(0, events.Calls);
    }

    private sealed class QuietThing : Thing
    {
        public int Reads;
        [Cfet2Status, Cfet2NoEvent] public int Value { get { Reads++; return 8; } }
    }
    private sealed class RejectEventRegistration : IEventCommunicationModule
    {
        public int Calls;
        public override Jtext103.CFET2.Core.Config.EventTransport? Transport => Jtext103.CFET2.Core.Config.GlobalConfig.HttpEventSubscriptionTransport;
        public override string[] ProtocolNames => new[] { "http" };
        public override string GetFullHost(string protocol, string host, int port) => $"http://{host}:{port}";
        public override void Subscribe(Token token, EventFilter filter) { Calls++; Assert.Fail("NoEvent must not register events"); }
        public override void UnSubScribe(Token token) { }
        public override void RegisterEventCallback(Action<EventArg> handler) { }
        public override void Dispose() { }
    }
}
