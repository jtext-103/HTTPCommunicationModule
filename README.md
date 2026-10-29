# CFET2 HTTP Communication Module

## Cfet2NoEvent

HTTP JSON/MessagePack 资源响应始终携带资源目录确定的 `NoEvent` 字段，不依赖 `ResourceInfoMidware`。读取、写入行为保持正常。新 Core 的远程 `SubscribeResource` 允许一次 GET，然后在 `NoEvent=true` 时返回 `Skipped`，不注册事件，不启动轮询。


CFET2 的 HTTP 通信模块。它将 HTTP 请求映射为本地 `Hub` 资源访问，也让 CFET2 节点通过 `http://` URI 访问远程资源。

## 功能

- 注册 `http` 协议，支持 HTTP Server 与远程资源访问。
- 将 `GET`、`POST`、`PUT` 分别映射为 `get`、`set`、`invoke`。
- 返回完整 `ISample.Context`，支持 JSON 和 MessagePack。
- 支持 URI path/query、JSON 数组、JSON 对象和 CFET2 输入信封。
- 默认启用 CORS 并处理 `OPTIONS` 预检请求。
- 兼容旧 Nancy HTTP CM 的主要协议边界。

## 快速开始

安装依赖：

```powershell
dotnet add package CFET2Core --version 3.3.0
dotnet add package Jtext103.CFET2.HTTPCommunicationModule --version 1.1.3
```

注册并启动模块：

```csharp
using Jtext103.CFET2.HTTPCommunicationModule;

var http = new HTTPCommunicationModule(new Uri("http://0.0.0.0:8001/"));
host.MyHub.TryAddCommunicationModule(http);
host.MyHub.StartCommunication();
```

## 配置与扩展

- 当前只注册 `http`；不提供 HTTPS、TLS、认证或授权能力。
- 可通过 `CFET2_WEB_UI_ROOT`，或程序/当前目录下的 `views` 提供 Web UI 静态资源。
- 非法 JSON body 当前可能导致 500 响应；这是已知边界，暂不修复。
- 源码测试需要 .NET 10 SDK：`dotnet test .\TestCode\TestCode.csproj`。

## 文档

- [实现说明与测试边界](Doc/HTTPCM实现说明与测试边界.md)

## 构建与发布

当前 `.gitlab-ci.yml` 在默认分支执行版本更新、Release 测试、构建和打包，并在 publish 阶段推送 NuGet。Core、HTTP、WebSocket、MQTT 按此顺序处理依赖，先确认所需 Core 包可还原，再更新下游固定包版本。

提交说明末尾的 `-a` 升主版本并清零次版本和修订号，`-b` 升次版本并清零修订号；没有这两个末尾标记时升修订号。独立的 `-notest` 标记跳过测试，但仍执行构建、打包和发布；组合示例为 `发布更新 -notest -b`。CI 默认遍历仓库内测试项目，执行 `dotnet test --configuration Release`；没有配置真实 Broker 开关和工具时，不能将流水线通过视为 MQTT 网络集成验证通过。
