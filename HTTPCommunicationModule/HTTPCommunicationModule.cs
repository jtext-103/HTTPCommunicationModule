using Jtext103.CFET2.Core.Communication;
using Jtext103.CFET2.Core.Sample;

namespace Jtext103.CFET2.HTTPCommunicationModule
{
    public partial class HTTPCommunicationModule : CommunicationModule, IDisposable
    {
        public override string[] ProtocolNames => ["http"];

        readonly Uri uri;
        readonly string accept = "json";

        private HTTPServer? server;
        private bool disposed;

        public HTTPCommunicationModule(Uri _uri, string _accept = "JSON")
        {
            uri = _uri;
            accept = _accept;
        }


        public override void Start()
        {
            base.Start();
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(HTTPCommunicationModule));
            }

            server ??= new HTTPServer(uri, MyHub);
            server.Start();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            server?.Dispose();
            server = null;
            disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
