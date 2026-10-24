# HTTPCM 实现说明与测试边界

## 目标

本文档说明 `HTTPCommunicationModule` 需要基于新版 CFETCore 实现的 HTTP Communication Module 能力。当前 HTTP 资源访问链路已实现；项目版本为 1.0.2，固定依赖 Core 3.2.0，目标框架为 net10.0。下文设计要求与历史验证记录保留原阶段含义，当前边界以实现及对应测试为准。

HTTPCM 的核心目标是：

1. 作为 `CommunicationModule` 的 HTTP 实现，注册到 CFETCore 的 `CommunicationManager`。
2. 在本机启动 HTTP Server，把外部 HTTP 请求转换为本机 `Hub` 资源访问。
3. 在访问远程 `http://` URI 时，把 `Hub` 传入的资源访问转换为 HTTP Client 请求。当前实现只注册 `http` 协议；`https` 出站/入站能力列为后续可能增加的功能。
4. 在网络边界上完整传输 `ISample.Context`，使调用方拿到的结果仍能按 `ISample` 使用。
5. 保持与历史 Nancy HTTP CM 的主要语义兼容：`GET=get`，`POST=set`，`PUT=invoke`。
6. 新 HTTPCM 必须能与旧 `NancyHttpCommunicationModule` 通讯。旧实现位置：

```text
E:\project\cfet2update\newcfet2\cfet2core\jtextcfet2core\Code\NancyHttpCommunicationModule
```

兼容目标包括：

1. 新 Client 能访问旧 Nancy Server。
2. 旧 Nancy Client 能访问新 Server。
3. 新 Server 返回的 sample context 能被旧 Client 反序列化为 `ISample`。
4. 旧 Server 返回的 sample context 能被新 Client 反序列化为 `ISample`。

## 参考的 CFETCore 契约

HTTPCM 必须继承并实现：

```csharp
Jtext103.CFET2.Core.Communication.CommunicationModule
```

必须实现的成员来自 `CommunicationModule`：

```csharp
public abstract string[] ProtocolNames { get; }

public virtual void Start();

public abstract ISample TryGetResourceSampleWithUri(string requestUri, Dictionary<string, object> inputDict);
public abstract ISample TryGetResourceSampleWithUri(string requestUri, params object[] inputs);
public abstract ISample TryInvokeSampleResourceWithUri(string requestUri, Dictionary<string, object> inputDict);
public abstract ISample TryInvokeSampleResourceWithUri(string requestUri, params object[] inputs);
public abstract ISample TrySetResourceSampleWithUri(string requestUri, Dictionary<string, object> inputDict);
public abstract ISample TrySetResourceSampleWithUri(string requestUri, params object[] inputs);
```

Core 侧调用关系如下：

```text
远程资源访问:
Hub.TryAccessResourceSampleWithUri(ResourceRequest)
  -> Hub.TryGet/TrySet/TryInvokeResourceSampleWithUri(...)
  -> CommunicationManager.GetModuleFor(uri.Scheme)
  -> HTTPCM.TryGet/TrySet/TryInvoke...

HTTP 入站资源访问:
HTTP Server 收到请求
  -> 构造 ResourceRequest
  -> MyHub.TryAccessResourceSampleWithUri(request)
  -> 本地资源 Get/Set/Invoke
  -> Pipeline.BatchProcess(...)
  -> 返回 ISample.Context
```

需要注意：

1. `CommunicationManager` 会通过 `HubMaster.InjectHubToModule(module)` 给 HTTPCM 注入 `MyHub`。
2. `StartCommunication()` 会调用 `CommunicationManager.StartAll()`，最终调用 HTTPCM 的 `Start()`。
3. `Start()` 要做到幂等：重复调用不能重复绑定端口、重复启动后台服务或破坏已有实例。

## 模块职责

HTTPCM 需要同时包含 Client 和 Server 两部分。

## 旧 Nancy HTTP CM 协议兼容要求

旧 `NancyHttpCommunicationModule` 是当前 HTTP 通讯协议的兼容基线。新 HTTPCM 可以增加能力，但不能破坏旧协议。

### 旧协议行为摘要

旧 Client 行为：

1. `TryGetResourceSampleWithUri(...)` 发送 HTTP `GET`。
2. `TrySetResourceSampleWithUri(...)` 发送 HTTP `POST`。
3. `TryInvokeSampleResourceWithUri(...)` 发送 HTTP `PUT`。
4. 请求默认 `Accept: application/JSON`。
5. 当 `AcceptEncoding == "MessagePack"` 时，请求设置：

```text
Accept-Encoding: MessagePack
Accept: application/MessagePack
```

6. 请求 `ContentLength = 0`，不发送 body。因此旧 Client 只支持 URI path/query 形式的参数。

旧 Server 行为：

1. `GET /{name*}` 映射为 `AccessAction.get`。
2. `POST /{name*}` 映射为 `AccessAction.set`。
3. `PUT /{name*}` 映射为 `AccessAction.invoke`。
4. Server 使用 `Request.Url.Path + Request.Url.Query` 构造 `ResourceRequest`。
5. Server 不解析 body。
6. Server 返回 `ISample.Context`，JSON 默认返回 `application/json; charset=utf-8`。
7. 当请求包含 `Accept-Encoding: MessagePack` 或 `Accept: application/MessagePack` 时，Server 返回 MessagePack 编码的 `ISample.Context`。
8. 旧 Server 会把返回 sample 标记为 remote：

```text
CFET2CORE_SAMPLE_ISREMOTE = true
```

9. 旧 Server 在请求带 `Referrer` 时，会把 sample path 改成带 scheme、host、port 的绝对 URL。
10. 旧 Server 会识别浏览器页面请求：当 `Accept` 包含 `text/html` 时，不走资源 API，而是 redirect 到 Web UI hash 路径。
11. 旧 Server 普通浏览器请求 redirect 目标形如：

```text
/views/index.html#/{requestPath}{queryString}
```

12. 旧 Server 对包含 `WindowsAppIndex` 的浏览器请求 redirect 目标形如：

```text
/views/WindowsAppIndex.html#/{subRequestPath}{queryString}
```

13. 旧 Server 对 `ResourceDoesNotExistException` 返回 404，对其他异常返回 400。

### 新实现必须兼容的部分

新 HTTPCM 必须保持：

1. HTTP Method 映射兼容：`GET=get`，`POST=set`，`PUT=invoke`。
2. URI path/query 参数兼容：旧 Client 发来的无 body 请求必须能正常访问资源。
3. JSON 响应兼容：响应内容必须是 sample context JSON，而不是只返回 sample value。
4. sample context key 兼容：必须保留 `CFET2CORE_SAMPLE_*` 字段名称。
5. dictionary key 大小写不敏感兼容：新 Client 反序列化旧响应后也应使用大小写不敏感 dictionary。
6. MessagePack 协商兼容：当旧 Client 请求 MessagePack 时，新 Server 必须能返回旧 Client 可反序列化的 MessagePack sample context。
7. 新 Client 必须能读取旧 Server 的 JSON 响应。
8. 新 Client 如果启用 MessagePack，也必须能读取旧 Server 的 MessagePack 响应。
9. HTTP 状态码兼容：404 和 400 语义至少要与旧 Server 保持一致。

### 新增能力与旧协议的边界

新 HTTPCM 可以新增 body 输入协议、CORS、`IDisposable`、更明确的错误处理等能力，但必须遵守以下边界：

1. 新 Client 访问旧 Nancy Server 时，不得依赖 body/envelope 输入；旧 Server 不解析 body。
2. 如果需要访问旧 Server，参数应编码到 URI path 或 query 中。
3. body envelope、JSON 数组 fallback、普通 JSON 对象 fallback 只保证新 Server 支持；旧 Server 不兼容这些输入方式。
4. 新 Server 面向旧 Client 时，必须接受无 body 请求，并按旧 path/query 方式处理。
5. 新 Server 可以比旧 Server 更完整地为 JSON 响应添加 CORS headers，这不破坏旧 Client。
6. 新 Server 必须支持浏览器 `Accept: text/html` 的 Web UI hash redirect 行为，用于兼容旧 Web UI 入口。

### Server 职责

Server 负责接收外部 HTTP 请求，并把请求还原为 CFETCore 本地资源访问。

需要实现：

1. 监听配置的本机 HTTP 地址，例如 `http://0.0.0.0:8001/` 或 `http://localhost:8001/`。
2. 本阶段 Server 端只强制支持 `http://` 监听，不要求启动 `https://` Server。原因是 HTTPS Server 需要证书绑定、证书加载和信任链配置，这些属于独立的 TLS 配置问题。
3. 根据 HTTP Method 映射 `AccessAction`：

```text
HTTP GET  -> AccessAction.get
HTTP POST -> AccessAction.set
HTTP PUT  -> AccessAction.invoke
```

4. 使用请求 path 和 query 构造资源 URI：

```text
/{thing}/{resource}/{routeInput}?name=value
```

5. 构造 `ResourceRequest` 并调用：

```csharp
MyHub.TryAccessResourceSampleWithUri(request)
```

6. 返回 `ISample.Context`，而不是只返回 `ObjectVal`。
7. 对返回的 sample 标记远程来源：

```csharp
sample.Context[SampleBase<object>.KeyOfIsRemote] = true;
```

或通过 `ISample.IsRemote = true` 完成。

8. 如果需要给浏览器或远程调用方返回可定位路径，应保证 `CFET2CORE_SAMPLE_PATH` 可用。基础 API 返回相对资源路径即可；如后续 Web UI 需要绝对路径，可单独定义浏览器分支。
9. 当前阶段需要支持浏览器跨域访问。Server 必须默认开启 CORS，且第一版不提供关闭开关。
10. CORS 响应至少需要包含：

```text
Access-Control-Allow-Origin: *
Access-Control-Allow-Methods: GET, POST, PUT, OPTIONS
Access-Control-Allow-Headers: Content-Type, Accept, Accept-Encoding, Authorization, X-Requested-With
```

11. Server 必须处理浏览器预检 `OPTIONS` 请求。预检请求不进入 CFETCore 资源访问链路，直接返回成功状态和 CORS headers。
12. 当前阶段需要支持 Web UI 和浏览器 hash redirect。浏览器页面请求与资源 API 请求必须分流处理。
13. 当请求 `Accept` 包含 `text/html` 时，应按浏览器页面请求处理，不进入 CFETCore 资源访问链路。
14. 普通浏览器页面请求应 redirect 到：

```text
/views/index.html#{原始请求 path}{原始 query}
```

例如：

```text
GET /Card0/LatestData?channel=1
Accept: text/html

302/301 Location: /views/index.html#/Card0/LatestData?channel=1
```

15. 包含 `WindowsAppIndex.html` 的浏览器页面请求应兼容旧 Nancy 行为，redirect 到：

```text
/views/WindowsAppIndex.html#{去掉 /WindowsAppIndex.html 后的 path}{原始 query}
```

如果去掉后的 path 为空，则 hash path 使用 `/`。
16. Server 需要提供 Web UI 静态文件服务，至少能返回 `/views/index.html` 和 `/views/WindowsAppIndex.html`。静态资源文件的来源可以是模块内嵌资源、发布目录或可配置目录，但路径行为必须稳定。

### Client 职责

Client 负责把 `Hub` 转发过来的远程资源访问转换为 HTTP 请求。

需要实现：

1. `ProtocolNames` 当前只包含：

```csharp
new[] { "http" }
```

当前 HTTPCM 只通过 `CommunicationManager` 接管 `http://` URI。`https://` 出站访问和 HTTPS Server 入站监听都不属于当前实现，后续可作为独立 TLS/证书配置能力增加。

2. `TryGetResourceSampleWithUri(...)` 使用 HTTP `GET`。
3. `TrySetResourceSampleWithUri(...)` 使用 HTTP `POST`。
4. `TryInvokeSampleResourceWithUri(...)` 使用 HTTP `PUT`。
5. 远程响应反序列化为 `ISample`，本质上是还原 `SampleBase<object>.Context`。
6. 反序列化后的 `Context` 必须使用大小写不敏感的 dictionary，保持 `SampleBase<T>` 的现有访问习惯：

```csharp
new Dictionary<string, object>(StringComparer.InvariantCultureIgnoreCase)
```

7. 返回前设置：

```csharp
result.IsRemote = true;
```

8. 网络错误、超时、HTTP 非成功状态、响应无法反序列化时，需要给出清晰异常或 invalid sample。建议：

```text
HTTP 404 -> ResourceDoesNotExistException 或映射为 invalid sample
HTTP 400 -> BadResourceRequestException 或带错误消息的 invalid sample
5xx/连接失败/超时 -> 抛出通信异常，错误信息包含 method、uri、status/exception
```

当前 Core 对远程 CM 的抽象方法没有定义统一通信异常类型，因此第一版可以抛出 `Exception`，但错误信息必须可定位。

## 参数传输规则

CFETCore 支持三种参数来源：

1. URI route input：`/thing/status/1/c`
2. query string input：`/thing/status?channel=1&unit=c`
3. 显式 array/dictionary input：`TryGet(..., object[] inputs)` 或 `TryGet(..., Dictionary<string, object> inputDict)`

HTTPCM 需要明确网络边界上的参数传输方式。

### URI 内参数

URI path 和 query 必须原样传输。Server 端把收到的 path/query 交给 Core，由 `Hub.ParseLocalRequest(...)` 按现有规则解析：

1. 从完整 path 向上回退匹配真实资源路径。
2. 多余 path segment 作为 route input。
3. query string 作为 dictionary input。
4. 当 dictionary input 存在时，route input 会合并到 `CommonConstants.TheLastInputsKey`。

### 显式 array/dictionary input

新版 HTTPCM 不能只支持 URI 内参数，因为 `CommunicationModule` 抽象接口已经暴露了 array/dictionary 两组输入。

建议第一版定义以下 HTTP body 协议。CFET 节点之间互相通信时，优先使用完整信封格式，保证 array/dictionary 输入的语义严谨可判定：

```json
{
  "usingInputDict": false,
  "inputArray": [1, "c"],
  "inputDict": null
}
```

或：

```json
{
  "usingInputDict": true,
  "inputArray": null,
  "inputDict": {
    "channel": 1,
    "unit": "c"
  }
}
```

Client 规则：

1. 如果 array/dictionary 为空，只发送 URI，不发送 body。
2. 如果 `inputs` 非空，发送 `usingInputDict=false` 和 `inputArray`。
3. 如果 `inputDict` 非空，发送 `usingInputDict=true` 和 `inputDict`。
4. 当前 Client 对 GET/POST/PUT 的非空显式 array/dictionary 输入统一发送 JSON 信封 body，Server 按同一协议解析；GET 显式输入并非未实现。无显式输入时仍使用 URI path/query。GET body 的代理和第三方客户端兼容性需按部署环境验证；旧 Nancy Server 不解析该 body，访问旧服务仍须使用 URI 参数。

Server 规则：

1. 没有 body 时，按 path/query 构造：

```csharp
new ResourceRequest(uri, action, null, null, null)
```

2. 有 body 且 `usingInputDict=false` 时，构造：

```csharp
new ResourceRequest(uri, action, inputArray, null, null, false)
```

3. 有 body 且 `usingInputDict=true` 时，构造：

```csharp
new ResourceRequest(uri, action, null, inputDict, null, true)
```

4. 有 body 且 body 是 JSON 数组时，按 array input 处理。该规则面向 Python 脚本、Postman、前端等第三方客户端：

```json
[1, "c"]
```

等效于：

```csharp
new ResourceRequest(uri, action, inputArray, null, null, false)
```

5. 有 body 且 body 是普通 JSON 对象，并且不包含 `usingInputDict` 字段时，按 dictionary input 处理：

```json
{
  "channel": 1,
  "unit": "c"
}
```

等效于：

```csharp
new ResourceRequest(uri, action, null, inputDict, null, true)
```

6. 有 body 且包含 `usingInputDict` 字段时，必须按 CFET 信封协议解析。信封格式优先级最高，不能再套用普通对象 fallback。

这种 fallback 机制只影响 HTTP Server 对第三方请求的易用性，不改变 CFET 节点之间的严格传输协议。

## 序列化协议

第一版必须支持 JSON。为了兼容旧 `NancyHttpCommunicationModule`，还必须支持旧协议的 MessagePack 协商路径。

### JSON 响应

默认响应：

```text
Content-Type: application/json; charset=utf-8
```

响应内容为 sample context：

```json
{
  "CFET2CORE_SAMPLE_VAL": 123,
  "CFET2CORE_SAMPLE_ISVALID": true,
  "CFET2CORE_SAMPLE_PATH": "/thing/status",
  "CFET2CORE_SAMPLE_RESOURCETYPE": 1,
  "CFET2CORE_SAMPLE_ISREMOTE": true
}
```

必须保留的关键字段来自 `SampleBase<T>`：

```text
CFET2CORE_SAMPLE_VAL
CFET2CORE_SAMPLE_ISVALID
CFET2CORE_SAMPLE_ERRORMESSAGE
CFET2CORE_SAMPLE_PATH
CFET2CORE_SAMPLE_CHILDTHING
CFET2CORE_SAMPLE_PARAMETERSLIST
CFET2CORE_SAMPLE_RESOURCETYPE
CFET2CORE_SAMPLE_ISREMOTE
```

### MessagePack 兼容路径

历史 Nancy CM 支持通过 `Accept-Encoding: MessagePack` 或 `Accept: application/MessagePack` 选择 MessagePack。新版需要保持以下边界：

1. JSON 是默认和必测路径。
2. MessagePack 是协商路径，只有请求明确要求时才返回。
3. MessagePack 返回也必须传输完整 `ISample.Context`。
4. 客户端按 `Content-Type` 或 `Content-Encoding` 选择反序列化器。
5. 为兼容旧 Server，新 Client 至少要识别旧实现可能返回的 `Content-Encoding: MessagePack`。
6. 为兼容旧 Client，新 Server 在收到 `Accept-Encoding: MessagePack` 或 `Accept: application/MessagePack` 时，要返回旧 Client 可解析的 MessagePack dictionary。
7. 当前实现返回 MessagePack 时同时设置：

```text
Content-Encoding: MessagePack
Content-Type: application/MessagePack
```

旧 Nancy Client 只通过 `Content-Type.Contains("MessagePack")` 判断是否按 MessagePack 反序列化，因此 `Content-Type` 必须包含 `MessagePack`。

## HTTP 状态码建议

Server 端建议状态码：

```text
200 OK: Core 成功返回 sample，即使 sample.IsValid=false 也可以是 200
400 Bad Request: 请求格式错误、body 无法解析、HTTP method 不支持当前动作
404 Not Found: Core 抛出 ResourceDoesNotExistException
405 Method Not Allowed: 非 GET/POST/PUT 的资源访问请求
204 No Content: CORS 预检 OPTIONS 请求成功
500 Internal Server Error: 未预期异常
```

说明：CFETCore 的资源实现失败通常会返回 invalid sample，而不是抛异常。这类 invalid sample 不应被 HTTP CM 强行改成 500，否则会破坏 Core 的 sample 语义。

所有 Server 响应都应附带 CORS headers，包括成功响应、错误响应和预检响应。

当前实现注意：`BuildResourceRequestAsync(...)` 在进入资源访问 `try/catch` 前执行，因此非法 JSON body 抛出的 `BadResourceRequestException` 当前会由 ASP.NET Core 管道表现为 500；该未处理异常路径也可能拿不到自定义 CORS headers。后续若要完全符合上表建议，应把请求构造和 body 解析错误也纳入 400 处理。

## 生命周期与配置

HTTPCM 需要提供最小配置：

```text
ListenUri: 本机 HTTP 监听地址，第一版只要求 http://
DefaultAccept: 默认响应编码，第一版为 JSON
Timeout: Client 请求超时
CorsEnabled: 当前阶段固定为 true，不提供关闭配置
WebUiRoot: Web UI 静态文件根目录或内嵌资源根路径
EnableWebUiRedirect: 当前阶段固定为 true，不提供关闭配置
```

`Start()` 要求：

1. 使用已注入的 `MyHub`。
2. 启动 HTTP Server。
3. 重复调用安全。
4. 启动失败时错误信息包含监听地址和底层异常。
5. 如果实现了后台任务，需要能在进程退出或模块释放时停止。
6. 虽然 `CommunicationModule` 基类没有要求 `Dispose`，HTTPCM 类自身仍建议实现 `IDisposable`。`Dispose()` 中应优雅停止 HTTP Server、释放监听句柄和 `HttpClient`/host 等资源。
7. 单元测试和集成测试必须能够在每个测试结束后释放端口，避免 TCP 端口被占用导致后续测试失败。xUnit/NUnit 或依赖注入容器调用 `Dispose()` 时，应能完成清理。

## 不属于 HTTPCM 当前阶段的内容

以下内容明确不纳入本阶段：

1. 权限、认证、鉴权、TLS 证书管理。
2. 复杂对象跨语言类型保真保证。第一版只保证 .NET CFET 节点之间按 JSON sample context 往返。
3. `https://` 协议注册、HTTPS Client 路由、HTTPS Server 监听。该能力可在后续版本作为 TLS/证书配置完善后增加。

## 测试边界

测试需要覆盖 HTTPCM 与 CFETCore 的契约边界，而不是测试 Thing 内部业务逻辑。

### 1. 模块注册与生命周期

需要验证：

1. `ProtocolNames` 当前包含 `http`，不要求包含 `https`。
2. `Hub.TryAddCommunicationModule(httpCm)` 后，`http://...` 请求能路由到 HTTPCM。
3. `StartCommunication()` 会启动 HTTPCM Server。
4. `Start()` 重复调用不会重复监听或抛出无意义异常。
5. 未调用 `Start()` 时，Client 侧远程访问行为明确：要么连接失败，要么返回清晰错误。
6. 调用 `Dispose()` 后释放监听端口；同一端口可被下一个测试重新启动。
7. Server 入站监听当前只测试 `http://`；`https://` Server 属于后续可能增加的功能。

不需要验证：

1. `CommunicationManager` 自身 dictionary 的内部实现。

### 2. HTTP Method 到 AccessAction 的映射

需要验证：

1. `GET /thing/status` 调用本地 get。
2. `GET /thing/config` 调用本地 get。
3. `POST /thing/config/{value}` 调用本地 set。
4. `PUT /thing/method/{arg}` 调用本地 invoke。
5. 用错误 method 访问资源时行为正确，例如 `POST` status、`PUT` config，应返回 invalid sample 或 400，不能静默成功。

不需要验证：

1. DELETE/PATCH 等非设计动作。

### 3. URI 参数解析

需要验证：

1. route input：`/thing/status/1/c`。
2. query input：`/thing/status?channel=1&unit=c`。
3. query 覆盖显式 dictionary input 的规则，或按文档定义的优先级执行。
4. 多余 route segment 在 dictionary 模式下合并到 `CommonConstants.TheLastInputsKey`。
5. 大小写不敏感资源路径。
6. URL 编码字符能正确解码，例如空格、中文、`/` 的转义形态。

不需要验证：

1. `Hub.ParseLocalRequest(...)` 的所有内部组合；这些属于 Core 单元测试。
2. Thing 方法参数默认值的完整排列组合；只需覆盖 HTTP CM 是否把参数交给 Core。

### 4. Client 出站访问

需要验证：

1. `MyHub.Get("http://host:port/thing/status")` 发送 HTTP GET。
2. `MyHub.Set("http://host:port/thing/config/2")` 发送 HTTP POST。
3. `MyHub.Invoke("http://host:port/thing/method/a")` 发送 HTTP PUT。
4. 远程返回 sample context 后，Client 能还原为 `ISample`。
5. `IsRemote == true`。
6. `Path`、`ResourceType`、`IsValid`、`ErrorMessages`、`ObjectVal` 可按 Core sample 语义读取。

不需要验证：

1. `HttpClient` 或底层网络库本身。
2. 跨公网、代理、DNS、HTTPS/TLS 场景。

### 5. Server 入站访问

需要验证：

1. 第三方 HTTP Client 不经过 CFET Hub，直接访问 Server，也能得到 sample context JSON。
2. `Accept: application/json` 或未指定 Accept 时返回 JSON。
3. 不存在资源返回 404。
4. Core 返回 invalid sample 时，HTTP 仍返回可反序列化 sample context。
5. Server 端不会把远程 URI 再当远程转发；入站 path/query 应作为本地资源路径处理。
6. 第三方客户端发送 JSON 数组 body 时，Server 按 array input 解析。
7. 第三方客户端发送不含 `usingInputDict` 的普通 JSON 对象 body 时，Server 按 dictionary input 解析。
8. CFET 客户端发送含 `usingInputDict` 的信封 body 时，Server 按信封协议解析，且优先级高于 fallback。
9. 浏览器跨域请求能成功拿到 CORS headers。
10. `OPTIONS` 预检请求返回成功状态和 CORS headers，且不调用 `MyHub.TryAccessResourceSampleWithUri(...)`。
11. 404、400、500 等错误响应也包含 CORS headers，避免浏览器端只能看到跨域错误而看不到真实 HTTP 状态。

不需要验证：

1. Web UI 前端业务功能是否完整。
2. Web UI 内部路由、页面交互和视觉样式。

### 5.1 Web UI 与浏览器 hash redirect

需要验证：

1. `Accept: text/html` 请求 `/` 时，Server 返回 Web UI redirect。
2. `Accept: text/html` 请求 `/Card0/LatestData?channel=1` 时，redirect 到 `/views/index.html#/Card0/LatestData?channel=1`。
3. `Accept: text/html` 请求 `/WindowsAppIndex.html` 时，redirect 到 `/views/WindowsAppIndex.html#/`。
4. `Accept: text/html` 请求 `/WindowsAppIndex.html/Card0/LatestData?channel=1` 时，redirect 到 `/views/WindowsAppIndex.html#/Card0/LatestData?channel=1`。
5. 浏览器页面请求不调用 `MyHub.TryAccessResourceSampleWithUri(...)`。
6. `/views/index.html` 能返回 Web UI 静态文件。
7. `/views/WindowsAppIndex.html` 能返回 Web UI 静态文件。
8. 非 HTML API 请求仍按资源 API 处理，例如 `Accept: application/json` 不触发 hash redirect。
9. Web UI redirect 响应也包含 CORS headers。

不需要验证：

1. Web UI 页面内部功能。
2. 前端构建链路。
3. 静态资源缓存策略。

### 6. 序列化边界

需要验证：

1. 基础类型：int、double、bool、string。
2. null 值。
3. 数组或列表值。
4. 简单 POCO 值。
5. invalid sample 的错误消息列表。
6. dictionary key 大小写不敏感。

MessagePack 兼容需要验证：

1. Client 通过 Accept/Accept-Encoding 请求 MessagePack。
2. Server 返回 MessagePack。
3. Client 成功还原 sample context。
4. JSON 路径不受 MessagePack 支持影响。
5. 旧 Nancy Client 请求新 Server 的 MessagePack 响应时可以成功反序列化。
6. 新 Client 请求旧 Nancy Server 的 MessagePack 响应时可以成功反序列化。

不需要验证：

1. 任意复杂对象引用循环。
2. 非 .NET 客户端对复杂对象的强类型反序列化。

### 7. 错误与超时边界

需要验证：

1. 远程 host 不可达。
2. 远程端口未监听。
3. 请求超时。
4. HTTP 404。
5. HTTP 500。
6. 响应不是合法 JSON/sample context。
7. 请求 body 不是合法 JSON。

期望结果：

1. 错误信息包含 method 和 URI。
2. 不吞异常。
3. 不返回半初始化 sample。
4. Server 端异常不导致进程退出。

### 8. 并发边界

需要验证：

1. 多个并发 GET 请求。
2. GET 与 SET 并发时 HTTP CM 不共享错误的请求状态。
3. 多个远程 host 顺序访问不会污染上一次响应 context。
4. Server 后台任务异常可记录或暴露。

不需要验证：

1. Thing 自身线程安全。
2. 高压性能指标。
3. 连接池极限调优。

### 9. 新旧 HTTP CM 互通边界

需要验证：

1. 新 Client 访问旧 Nancy Server：`GET` status 成功。
2. 新 Client 访问旧 Nancy Server：`POST` config 成功。
3. 新 Client 访问旧 Nancy Server：`PUT` method 成功。
4. 旧 Nancy Client 访问新 Server：`GET` status 成功。
5. 旧 Nancy Client 访问新 Server：`POST` config 成功。
6. 旧 Nancy Client 访问新 Server：`PUT` method 成功。
7. 新旧互通时，参数使用 URI path/query，不依赖 body。
8. 新旧互通时，JSON sample context 字段完整。
9. 新旧互通时，`CFET2CORE_SAMPLE_ISREMOTE` 为 true。
10. 新旧互通时，404 和 400 行为符合旧协议预期。
11. 旧 Nancy Client 使用 `AcceptEncoding == "MessagePack"` 访问新 Server 时可以成功解析。
12. 新 Client 使用 MessagePack 访问旧 Nancy Server 时可以成功解析。

不需要验证：

1. 旧 Nancy Server 解析新 body envelope。
2. 旧 Nancy Server 解析 JSON 数组 fallback 或普通 JSON 对象 fallback。
3. 旧 Nancy Server 提供完整 CORS headers。
4. 旧 Nancy Server 的 Web UI 页面内部功能。

## 后续可能增加的功能

以下能力不属于当前实现，但可以作为后续增强：

1. `ProtocolNames` 增加 `https`，让 `CommunicationManager` 可把 `https://...` URI 路由到 HTTPCM。
2. HTTPS Client 出站访问的证书、代理、超时和错误映射配置。
3. HTTPS Server 入站监听，包括证书加载、端口绑定、信任链和部署文档。
4. 更细的错误响应体和异常类型映射，例如非法 JSON body 明确返回 400。

## 第一版验收标准

第一版 HTTPCM 完成后，至少应满足：

1. 可被 Core 注册并启动。
2. 本机 HTTP Server 能处理 `GET/POST/PUT` 资源请求。
3. CFET 节点可以通过 `MyHub.Get/Set/Invoke("http://...")` 访问另一个 CFET 节点资源。
4. JSON sample context 往返完整。
5. 不存在资源、错误动作、网络失败都有明确结果。
6. 默认支持浏览器跨域访问，并正确处理 CORS 预检请求。
7. 支持 Web UI 静态文件服务和浏览器 hash redirect。
8. 测试覆盖 Server 入站、Client 出站、参数解析、序列化、错误边界、CORS 边界、Web UI redirect 边界和新旧 HTTP CM 互通边界。

## 当前验证入口（2026-10-23）

从本仓库目录运行 `dotnet test TestCode/TestCode.csproj -c Release`，测试构建当前 HTTP 源码并还原固定的 Core NuGet 依赖。非法 JSON body 返回 500 的既有边界仍由 `InvalidJsonBody_CurrentlyReturnsInternalServerError` 覆盖；不应将该行为描述为已修复。CI 与包安装版本见 [README](../README.md)。本次仅同步文档，未产生新的测试通过记录。
