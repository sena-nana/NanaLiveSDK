# API 参考

## JavaScript 客户端（`@nanalive/sdk`）

源码位于仓库 [`SDK/js`](https://github.com/sena-nana/NanaLiveSDK/tree/main/SDK/js)，需要 Node.js ≥ 20。

### 协议常量

- `API_NAME`（`"NanaLiveControlAPI"`）、`API_VERSION`（`"2.0"`）
- `SUBPROTOCOL`（`"nanalive-control-v2"`）
- `DEFAULT_PORT`（`8312`）

### `createNanaLiveClient({ send, identity, token, onToken })`

创建控制 API 客户端。`send` 接收 MessagePack 编码后的二进制帧；`identity` 描述本插件（`pluginID`、`pluginName`、`pluginDeveloper`、`pluginVersion`、`scopes`），`pluginID` 请使用自己的反向域名标识，`scopes` 只申请实际用到的权限；`token` 传入已保存的授权 token，首次签发的 token 通过 `onToken` 回调返回，需用户在 NanaLive 插件页批准。

返回对象：

- `authenticate()`：完成鉴权（优先用已有 token，否则申请新 token）
- `request(messageType, data)`：发送任意请求并等待响应
- `receive(raw)`：把收到的二进制帧喂给客户端（内部按 `requestID` 配对响应）
- `listModels()` / `listMotions()` / `listExpressions()` / `listHotkeys()` / `listParameters()`：目录查询

### 助手

- `executableHotkeys(hotkeys)`：过滤出可执行的按键
- `parameterValueAfterTicks(parameter, ticks)`：按旋钮刻度计算参数目标值
- `writeParameterCommand(parameterID, value)`：构造 `ParameterWriteRequest`

### 会话层（`@nanalive/sdk/session`，仅 Node）

`createNanaLiveSession(...)` 在裸连接之上提供完整连接流程：建立 WebSocket → 鉴权（优先复用已有 token）→ 心跳保活。断线后挂起中的请求立即以 `connection_lost` 失败，并按指数退避（带抖动）自动重连与重新鉴权（token 跨重连复用）；通过 `onStatus` 回调观察 `connecting` / `connected` / `reconnecting` / `disconnected` 状态。

```js
import { createNanaLiveSession } from "@nanalive/sdk/session";

const session = createNanaLiveSession({
  identity,
  onToken: (token) => saveToken(token),
  onStatus: (status) => console.log(status),
});

await session.connect(); // 含重试；重试耗尽时抛出最后的错误
const models = await session.request("AvailableModelsRequest");
await session.close();
```

返回对象：`client`（底层协议客户端）、`connect()`（幂等，重复调用会重置会话）、`request(messageType, data)`、`close()`、`status`、`isConnected`。

选项（均有默认值）：`host` / `port` / `subprotocol`、`identity` / `token` / `onToken`、`onUnhandled` / `onError` / `onStatus`、`reconnect`（默认 `true`）、`maxRetries`（默认无限）、`retryDelay` / `maxRetryDelay`（500ms / 8s）、`heartbeatInterval` / `heartbeatTimeout`（10s / 5s，空闲超间隔发 WebSocket ping，超时内无任何入站帧即视为死链）、`connectTimeout`（5s）、`requestTimeout`（30s，`null` 关闭）。

### 连接

- `@nanalive/sdk/node-websocket` 导出 `connectBinaryWebSocket` / `connectTextWebSocket`（仅 Node 环境）；浏览器环境直接用全局 `WebSocket`。

示例：

```js
import { createNanaLiveClient, DEFAULT_PORT, SUBPROTOCOL } from "@nanalive/sdk";
import { connectBinaryWebSocket } from "@nanalive/sdk/node-websocket";

const socket = await connectBinaryWebSocket({
  host: "127.0.0.1",
  port: DEFAULT_PORT,
  subprotocol: SUBPROTOCOL,
  onMessage: (payload) => client.receive(payload),
});

const client = createNanaLiveClient({ send: (payload) => socket.send(payload), identity });
await client.authenticate();
const models = await client.listModels();
```

## 其他语言绑定

四个语言绑定的协议语义一致（常量、envelope、鉴权、助手函数），差异只在语言习惯：

| 绑定 | 安装 / 引用 | 连接方式 |
| --- | --- | --- |
| [Rust](https://github.com/sena-nana/NanaLiveSDK/tree/main/SDK/rust) | `nanalive-sdk = "0.1"` | `connect(ConnectOptions)` |
| [Python](https://github.com/sena-nana/NanaLiveSDK/tree/main/SDK/python) | `pip install nanalive-sdk`（备选网络库：`pip install "nanalive-sdk[aiohttp]"`） | `await connect(...)` |
| [C#](https://github.com/sena-nana/NanaLiveSDK/tree/main/SDK/csharp) | 引用 `Nanalive.Sdk` 项目 | `await NanaLiveConnection.ConnectAsync(...)` |

以 Python 为例的最小流程（其余语言见各自 README，结构相同）：

```python
from nanalive_sdk import connect, DEFAULT_PORT

connection = await connect(
    port=DEFAULT_PORT,
    identity={
        "pluginID": "dev.example.my-plugin",
        "pluginName": "My Plugin",
        "pluginDeveloper": "Example",
        "pluginVersion": "0.1.0",
        "scopes": ["model.read", "model.switch"],
    },
    on_token=save_token,
)
try:
    await connection.client.authenticate()
    models = await connection.client.list_models()
finally:
    await connection.close()
```

### 会话层（自动重连 + 心跳）

四个绑定都提供同语义的弹性会话：Rust 的 `NanaLiveSession`（`session` 模块）、
Python 的 `NanaLiveSession`（`nanalive_sdk.session`）、C# 的 `NanaLiveSession`，
选项与 JS 的 `createNanaLiveSession` 一致（`max_retries`、`retry_delay`、
`heartbeat_interval`、`connect_timeout`（默认 5s，覆盖建链+握手+鉴权）、
`request_timeout`、`on_status` 等）。以 Python 为例：

```python
from nanalive_sdk import NanaLiveSession

session = NanaLiveSession(
    port=DEFAULT_PORT,
    identity={...},
    on_token=save_token,
    on_status=lambda status: print(status),
)
await session.connect()  # 含重试；之后的断线由后台任务自动重连
models = await session.request("AvailableModelsRequest")
await session.close()
```

回调都在保护下调用：`on_status` / `on_unhandled` 抛出的异常经 `on_error` 上报，
不会打断自动重连；重连失败的原因（含重试耗尽）也会经 `on_error` 上报。

### Python 传输后端（按常用网络库选择）

Python 绑定的传输层可插拔，`connect()` 与 `NanaLiveSession` 均接受 `transport` 参数：

| `transport` | 网络库 | 安装 |
| --- | --- | --- |
| `"websockets"`（默认） | [websockets](https://pypi.org/project/websockets/) | 核心依赖，装完即用 |
| `"aiohttp"` | [aiohttp](https://pypi.org/project/aiohttp/) | `pip install "nanalive-sdk[aiohttp]"` |
| 自定义异步工厂 | 任意 | 返回带 `send`/`close`/异步迭代的适配对象，直接传入 |

```python
session = NanaLiveSession(..., transport="aiohttp")
```

会话未连接时 `request` 立刻失败（`NotConnectedError` / `NanaLiveError("not_connected")` /
`NanaLiveConnectionException` / `NanaLiveError::NotConnected`），超时失败为
`RequestTimeoutError` / `NanaLiveError::RequestTimeout` / `NanaLiveRequestTimeoutException`，
建链/鉴权超时为 `NanaLiveConnectionException("connect_timeout")` / `NanaLiveError::ConnectTimeout`。
差异：C# 的心跳映射为 `ClientWebSocket.KeepAliveInterval`，BCL 未暴露独立 pong 超时
（运行时按间隔一半判死，10s 间隔 ≈ 5s，与其他语言默认一致），
Python 的 `aiohttp` 后端 pong 判死窗口同为间隔一半。

注意：未配对的响应（服务器主动推送）仍会透传给调用方：Rust 的 `on_unhandled`
回调 / `receive` 返回值、Python 的 `on_unhandled` 回调 / `receive` 返回值、
C# 的 `OnUnhandled` 事件 / `Receive` 返回值、JS 的 `onUnhandled` 回调 / `receive` 返回值。

## 其他

Rust 侧的协议与消息定义目前随主程序仓库（`nanalive-plugin-api` crate）发布，正式外发到本仓库 `SDK/` 后会在此补充索引。
