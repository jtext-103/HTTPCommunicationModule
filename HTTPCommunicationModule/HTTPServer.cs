using Jtext103.CFET2.Core;
using Jtext103.CFET2.Core.Exception;
using Jtext103.CFET2.Core.Log;
using Jtext103.CFET2.Core.Resource;
using Jtext103.CFET2.Core.Sample;
using MessagePack;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jtext103.CFET2.HTTPCommunicationModule
{
    public class HTTPServer : IDisposable
    {
        private const string IndexPath = "/views/index.html";
        private const string WindowsIndexPath = "/views/WindowsAppIndex.html";
        private const string WebUiRootEnvironmentVariable = "CFET2_WEB_UI_ROOT";

        private WebApplication? _app;
        private bool disposed;

        private readonly Uri uri;
        private readonly Hub hub;
        private readonly ICfet2Logger logger = Cfet2LogManager.GetLogger("HTTPServer");

        public HTTPServer(Uri _uri, Hub _hub)
        {
            uri = _uri;
            hub = _hub;
        }

        public void Start()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(HTTPServer));
            }

            if (_app != null)
            {
                return;
            }

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls(uri.ToString());

            _app = builder.Build();
            _app.Use(AddCorsHeaders);
            UseWebUiStaticFiles(_app);

            _app.UseRouting();

            _app.MapMethods("/", ["OPTIONS"], HandleOptionsAsync);
            _app.MapMethods("/{**catchAll}", ["OPTIONS"], HandleOptionsAsync);
            _app.MapMethods("/", ["GET", "POST", "PUT"], HandleRequestAsync);
            _app.MapMethods("/{**catchAll}", ["GET", "POST", "PUT"], HandleRequestAsync);
            _app.MapMethods("/", ["DELETE", "PATCH", "HEAD"], HandleMethodNotAllowedAsync);
            _app.MapMethods("/{**catchAll}", ["DELETE", "PATCH", "HEAD"], HandleMethodNotAllowedAsync);

            _app.StartAsync().GetAwaiter().GetResult();
            logger.Info("HTTPServer running on " + uri);
            Console.WriteLine("HTTPServer running on "+uri);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            if (_app != null)
            {
                _app.StopAsync().GetAwaiter().GetResult();
                _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _app = null;
            }

            disposed = true;
            GC.SuppressFinalize(this);
        }

        private static async Task AddCorsHeaders(HttpContext context, Func<Task> next)
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers["Access-Control-Allow-Origin"] = "*";
                headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, OPTIONS";
                headers["Access-Control-Allow-Headers"] = "Content-Type, Accept, Accept-Encoding, Authorization, X-Requested-With";
                return Task.CompletedTask;
            });

            await next();
        }

        private static Task HandleOptionsAsync(HttpContext context)
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        }

        private static Task HandleMethodNotAllowedAsync(HttpContext context)
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return Task.CompletedTask;
        }

        private static void UseWebUiStaticFiles(WebApplication app)
        {
            string? webUiRoot = ResolveWebUiRoot();
            if (string.IsNullOrEmpty(webUiRoot))
            {
                return;
            }

            var provider = new PhysicalFileProvider(webUiRoot);
            var contentTypeProvider = new FileExtensionContentTypeProvider();

            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = provider,
                RequestPath = "/views",
                ContentTypeProvider = contentTypeProvider
            });

            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = provider,
                RequestPath = "",
                ContentTypeProvider = contentTypeProvider
            });
        }

        private static string? ResolveWebUiRoot()
        {
            string? configuredRoot = Environment.GetEnvironmentVariable(WebUiRootEnvironmentVariable);
            if (IsValidWebUiRoot(configuredRoot))
            {
                return Path.GetFullPath(configuredRoot!);
            }
            Console.WriteLine(AppContext.BaseDirectory);

            string appBaseViews = Path.Combine(AppContext.BaseDirectory, "views");
            if (IsValidWebUiRoot(appBaseViews))
            {
                return Path.GetFullPath(appBaseViews);
            }

            string currentDirectoryViews = Path.Combine(Directory.GetCurrentDirectory(), "views");
            if (IsValidWebUiRoot(currentDirectoryViews))
            {
                return Path.GetFullPath(currentDirectoryViews);
            }

            return null;
        }

        private static bool IsValidWebUiRoot(string? path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                Directory.Exists(path) &&
                File.Exists(Path.Combine(path, "index.html")) &&
                File.Exists(Path.Combine(path, "WindowsAppIndex.html"));
        }

        private static async Task WriteFallbackWebUiHtmlAsync(HttpContext context, string title)
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync($"""
                <!doctype html>
                <html>
                <head><meta charset="utf-8"><title>{title}</title></head>
                <body><div id="app">
                Check, don't find Views
                </div></body>
                </html>
                """);
        }

        private async Task HandleRequestAsync(HttpContext context)
        {
            var req = context.Request;
            var res = context.Response;

            var acceptHeaders = req.Headers["Accept"].ToString();
            if (acceptHeaders.Contains("text/html", StringComparison.InvariantCultureIgnoreCase))
            {
                string path = req.Path.Value ?? "";

                // 【判断 1】：如果是请求 index.html，且走到了这里，说明静态文件中间件没找到真实文件
                if (path.Equals(IndexPath, StringComparison.InvariantCultureIgnoreCase))
                {
                    res.StatusCode = StatusCodes.Status404NotFound;
                    await WriteFallbackWebUiHtmlAsync(context, "CFET2 Web UI - Check, don't find Views");
                    return;
                }

                // 【判断 2】：如果是请求 WindowsAppIndex.html 且没找到
                if (path.Equals(WindowsIndexPath, StringComparison.InvariantCultureIgnoreCase))
                {
                    res.StatusCode = StatusCodes.Status404NotFound;
                    await WriteFallbackWebUiHtmlAsync(context, "CFET2 Windows Web UI - Check, don't find Views");
                    return;
                }

                // 否则，正常重定向
                RedirectToWebUi(req, res);
                return;
            }

            AccessAction action = req.Method switch
            {
                "GET" => AccessAction.get,
                "POST" => AccessAction.set,
                "PUT" => AccessAction.invoke,
                _ => AccessAction.get
            };

            var resourceReq = await BuildResourceRequestAsync(req, action);
            ISample result;

            try
            {
                result = hub.TryAccessResourceSampleWithUri(resourceReq);
            }
            catch (ResourceDoesNotExistException)
            {
                res.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            catch (Exception)
            {
                res.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            result.Context["CFET2CORE_SAMPLE_ISREMOTE"] = true;

            string referer = req.Headers["Referer"].ToString();
            if (!string.IsNullOrEmpty(referer))
            {
                var originalPath = result.Context["CFET2CORE_SAMPLE_PATH"]?.ToString() ?? "";
                result.Context["CFET2CORE_SAMPLE_PATH"] = $"{req.Scheme}://{req.Host}{originalPath}";
            }

            await WriteSampleAsync(req, res, result);
        }

        private static void RedirectToWebUi(HttpRequest req, HttpResponse res)
        {
            string requestPath = req.Path.Value ?? "/";
            string queryString = req.QueryString.Value ?? "";
            string hashTag;

            if (requestPath.Contains("WindowsAppIndex", StringComparison.InvariantCultureIgnoreCase))
            {
                string subPath = requestPath.Length >= WindowsIndexPath.Length
                    ? requestPath.Substring(WindowsIndexPath.Length)
                    : "";
                if (string.IsNullOrEmpty(subPath))
                {
                    subPath = "/";
                }

                hashTag = WindowsIndexPath + "#" + subPath + queryString;
            }
            else
            {
                hashTag = IndexPath + "#" + requestPath + queryString;
            }

            res.Redirect(hashTag, permanent: false);
        }

        private static async Task<ResourceRequest> BuildResourceRequestAsync(HttpRequest req, AccessAction action)
        {
            string fullUri = (req.Path.Value ?? "/") + req.QueryString.Value;
            var body = await ReadBodyAsync(req);
            if (string.IsNullOrWhiteSpace(body))
            {
                return new ResourceRequest(fullUri, action, null, null, null);
            }

            try
            {
                var token = JToken.Parse(body);
                if (token is JArray array)
                {
                    return new ResourceRequest(fullUri, action, array.Select(ToPlainObject).ToArray(), null, null);
                }

                if (token is JObject obj)
                {
                    if (obj.TryGetValue("usingInputDict", StringComparison.InvariantCultureIgnoreCase, out var usingDictToken))
                    {
                        bool usingDict = usingDictToken.Value<bool>();
                        if (usingDict)
                        {
                            var dict = obj.TryGetValue("inputDict", StringComparison.InvariantCultureIgnoreCase, out var inputDictToken)
                                ? ToPlainDictionary(inputDictToken)
                                : new Dictionary<string, object>(StringComparer.InvariantCultureIgnoreCase);
                            return new ResourceRequest(fullUri, action, null, dict, null, true);
                        }

                        var inputArray = obj.TryGetValue("inputArray", StringComparison.InvariantCultureIgnoreCase, out var inputArrayToken)
                            ? ToPlainArray(inputArrayToken)
                            : [];
                        return new ResourceRequest(fullUri, action, inputArray, null, null, false);
                    }

                    return new ResourceRequest(fullUri, action, null, ToPlainDictionary(obj), null, true);
                }
            }
            catch (JsonException)
            {
                throw new BadResourceRequestException("Request body is not valid JSON.");
            }

            throw new BadResourceRequestException("Request body must be a JSON array or object.");
        }

        private static async Task<string?> ReadBodyAsync(HttpRequest req)
        {
            if (req.ContentLength == null || req.ContentLength == 0)
            {
                return null;
            }

            using var reader = new StreamReader(req.Body);
            return await reader.ReadToEndAsync();
        }

        private static async Task WriteSampleAsync(HttpRequest req, HttpResponse res, ISample result)
        {
            var acceptHeaders = req.Headers["Accept"].ToString();
            var acceptEncoding = req.Headers["Accept-Encoding"].ToString();

            if (acceptEncoding.Contains("MessagePack", StringComparison.InvariantCultureIgnoreCase) ||
                acceptHeaders.Contains("application/MessagePack", StringComparison.InvariantCultureIgnoreCase))
            {
                var bytes = MessagePackSerializer.Serialize(
                    result.Context,
                    MessagePack.Resolvers.ContractlessStandardResolver.Options);

                res.Headers["Content-Encoding"] = "MessagePack";
                res.ContentType = "application/MessagePack";
                await res.Body.WriteAsync(bytes);
                return;
            }

            res.ContentType = "application/json; charset=utf-8";
            await res.WriteAsync(JsonConvert.SerializeObject(result.Context));
        }

        private static object?[] ToPlainArray(JToken token)
        {
            return token is JArray array ? array.Select(ToPlainObject).ToArray() : [];
        }

        private static Dictionary<string, object> ToPlainDictionary(JToken token)
        {
            var dict = new Dictionary<string, object>(StringComparer.InvariantCultureIgnoreCase);
            if (token is not JObject obj)
            {
                return dict;
            }

            foreach (var prop in obj.Properties())
            {
                dict[prop.Name] = ToPlainObject(prop.Value)!;
            }

            return dict;
        }

        private static object? ToPlainObject(JToken token)
        {
            return token.Type switch
            {
                JTokenType.Object => ToPlainDictionary(token),
                JTokenType.Array => token.Select(ToPlainObject).ToArray(),
                JTokenType.Null => null,
                _ => ((JValue)token).Value
            };
        }
    }
}
