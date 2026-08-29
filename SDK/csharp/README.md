# Nanalive.Sdk (C# / .NET)

NanaLive 插件 API 的 C# 客户端绑定。连接 NanaLive 的本地控制 API（`ws://127.0.0.1:8312`，子协议 `nanalive-control-v2`，MessagePack 二进制帧），完成鉴权并调用模型、动作、表情、按键和参数接口。

需要 .NET 8+。协议数据统一用 `object` 树（`Dictionary<object, object?>` / 数组 / 标量）表示，由 MessagePack-CSharp 的 `PrimitiveObjectResolver` 编解码；WebSocket 用 BCL 的 `ClientWebSocket`。

## 安装

引用 `SDK/csharp/Nanalive.Sdk/Nanalive.Sdk.csproj` 项目，或发布为 NuGet 包 `Nanalive.Sdk` 后安装。

## 用法

```csharp
using Nanalive.Sdk;

await using var connection = await NanaLiveConnection.ConnectAsync(new ConnectOptions
{
    Identity = new Identity(
        "dev.example.my-plugin", "My Plugin", "Example", "0.1.0",
        ["model.read", "model.switch"]),
    OnToken = token => SaveToken(token),
});

await connection.Client.AuthenticateAsync();
var models = await connection.Client.ListModelsAsync();
```

`Identity` 的 `PluginId` 请使用自己的反向域名标识，`Scopes` 只申请实际用到的权限；首次申请的 token 经 `OnToken` 回调交付，需要用户在 NanaLive 插件页批准，请在本地持久化并在下次连接时作为 `Token` 传入。

## 弹性会话（自动重连 + 心跳）

`NanaLiveSession` 在裸连接之上提供完整连接流程：建立 WebSocket → 鉴权 → 心跳保活；断线后挂起中的请求立即失败，按指数退避（带抖动）自动重连并重新鉴权（token 跨重连复用）。

```csharp
await using var session = await NanaLiveSession.ConnectAsync(new SessionOptions
{
    Identity = new Identity(
        "dev.example.my-plugin", "My Plugin", "Example", "0.1.0",
        ["model.read", "model.switch"]),
    OnToken = token => SaveToken(token),
    OnStatus = status => Console.WriteLine(status), // Connecting / Connected / Reconnecting / Disconnected
});

var models = await session.RequestAsync("AvailableModelsRequest");
```

成员：`Client`（底层协议客户端，token 跨重连复用）、`ConnectAsync()`（幂等）、
`RequestAsync(messageType, data)`、`CloseAsync()`（实现 `IAsyncDisposable`）、
`Status`、`IsConnected`。选项（均有默认值）：`Host`/`Port`、`Identity`/`Token`/`OnToken`、
`OnUnhandled`/`OnError`/`OnStatus`、`Reconnect`（默认 `true`）、`MaxRetries`（默认无限）、
`RetryDelay`/`MaxRetryDelay`（500ms/8s）、`HeartbeatInterval`（10s，映射为
`ClientWebSocket.KeepAliveInterval`，pong 超时由 .NET 运行时内部处理）、
`RequestTimeout`（30s，`null` 关闭）。

会话未连接时 `RequestAsync` 抛 `NanaLiveConnectionException("not_connected")`；超时抛
`NanaLiveRequestTimeoutException`；断线时挂起请求以 `NanaLiveConnectionException("connection_lost")` 失败。

## API 一览

- `NanaLiveConnection.ConnectAsync(...)`：建立 WebSocket 连接，返回
  `NanaLiveConnection`（`.Client` + `CloseAsync()`，实现 `IAsyncDisposable`）。
  入站 MessagePack 帧自动喂给客户端，未配对的推送经 `OnUnhandled` 回调透传。
- `NanaLiveClient`：与传输无关的协议客户端，也可自行注入 `send` 回调构造：
  `await RequestAsync(messageType, data)`、`Receive(bytes)`、
  `await AuthenticateAsync()`、`await ListModelsAsync() / ListMotionsAsync() /
  ListExpressionsAsync() / ListHotkeysAsync() / ListParametersAsync()`。
- `Mp`：MessagePack 值构造与读取（`Map`、`Array`、`Str`、`Num`、`Bool`、
  `GetField`、`TryString`、`TryNumber`、`TryList`）。
- 助手：`Helpers.ExecutableHotkeys`、`Helpers.ParameterValueAfterTicks`、
  `Helpers.WriteParameterCommand`。
- 协议常量：`NanaLiveApi.ApiName / ApiVersion / Subprotocol / DefaultPort`。
- 异常：`NanaLiveApiException`（`.Code` 对应服务端 `errorCode`）及其
  `AuthenticationTokenMissingException`；连接层错误 `NanaLiveConnectionException`
  与 `NanaLiveRequestTimeoutException`。

裸连接（`NanaLiveConnection.ConnectAsync(...)`）只负责建立连接与泵任务；自动重连、
心跳与请求超时请使用 `NanaLiveSession`。

## 本地开发

```bash
dotnet test            # 运行测试
dotnet run --project examples/ListModels
```
