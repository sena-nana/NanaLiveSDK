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
| [Python](https://github.com/sena-nana/NanaLiveSDK/tree/main/SDK/python) | `pip install nanalive-sdk` | `await connect(...)` |
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

注意：

- 各绑定都不做自动重连与心跳，断线后请重建连接并重新 `authenticate()`
  （旧 token 仍有效时会直接验证通过）。
- 未配对的响应（服务器主动推送）会透传给调用方：Rust 的 `on_unhandled`
  回调 / `receive` 返回值、Python 的 `on_unhandled` 回调 / `receive` 返回值、
  C# 的 `OnUnhandled` 事件 / `Receive` 返回值、JS 的 `receive` 返回值。

## 其他

Rust 侧的协议与消息定义目前随主程序仓库（`nanalive-plugin-api` crate）发布，正式外发到本仓库 `SDK/` 后会在此补充索引。
