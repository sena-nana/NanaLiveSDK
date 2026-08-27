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

## 其他

Rust 侧的协议与消息定义目前随主程序仓库（`nanalive-plugin-api` crate）发布，正式外发到本仓库 `SDK/` 后会在此补充索引。
