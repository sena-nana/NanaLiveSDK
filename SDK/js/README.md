# @nanalive/sdk

NanaLive 插件 API 的 JavaScript 客户端绑定。连接 NanaLive 的本地控制 API（`ws://127.0.0.1:8312`，子协议 `nanalive-control-v2`，MessagePack 二进制帧），完成鉴权并调用模型、动作、表情、按键和参数接口。

需要 Node.js ≥ 20。

## 安装

第三方项目请从 npm 或 GitHub 安装；本仓库内的插件示例通过 `file:` 依赖引用本目录。

## 用法

```js
import { createNanaLiveClient, DEFAULT_PORT, SUBPROTOCOL } from "@nanalive/sdk";
import { connectBinaryWebSocket } from "@nanalive/sdk/node-websocket";

const socket = await connectBinaryWebSocket({
  host: "127.0.0.1",
  port: DEFAULT_PORT,
  subprotocol: SUBPROTOCOL,
  onMessage: (payload) => client.receive(payload),
});

const client = createNanaLiveClient({
  send: (payload) => socket.send(payload),
  identity: {
    pluginID: "com.example.my-plugin",
    pluginName: "My Plugin",
    pluginDeveloper: "Example",
    pluginVersion: "0.1.0",
    scopes: ["model.read", "model.switch"],
  },
  onToken: (token) => saveToken(token),
});

await client.authenticate();
const models = await client.listModels();
```

`identity` 中的 `pluginID` 请使用自己的反向域名标识，`scopes` 只申请实际用到的权限；首次申请的 token 需要用户在 NanaLive 插件页批准。

## 弹性会话（自动重连 + 心跳）

`createNanaLiveSession`（`@nanalive/sdk/session`，仅 Node）在裸连接之上提供完整连接流程：建立 WebSocket → 鉴权 → 心跳保活；断线后挂起中的请求立即失败，按指数退避自动重连并重新鉴权（token 跨重连复用）。

```js
import { createNanaLiveSession } from "@nanalive/sdk/session";

const session = createNanaLiveSession({
  identity,
  onToken: (token) => saveToken(token),
  onStatus: (status) => console.log(status), // connecting / connected / reconnecting / disconnected
});

await session.connect(); // 含重试；重试耗尽时抛出最后的错误
const models = await session.request("AvailableModelsRequest");
await session.close();
```

返回对象：`client`、`connect()`、`request(messageType, data)`、`close()`、`status`、`isConnected`。选项（均有默认值）：`host`/`port`/`subprotocol`、`identity`/`token`/`onToken`、`onUnhandled`/`onError`/`onStatus`、`reconnect`（默认 `true`）、`maxRetries`（默认无限）、`retryDelay`/`maxRetryDelay`（500ms/8s）、`heartbeatInterval`/`heartbeatTimeout`（10s/5s）、`connectTimeout`（5s）、`requestTimeout`（30s，`null` 关闭）。

## 导出

- `.`：协议常量（`API_NAME`、`API_VERSION`、`SUBPROTOCOL`、`DEFAULT_PORT`）、`createNanaLiveClient`，以及 `executableHotkeys`、`parameterValueAfterTicks`、`writeParameterCommand` 等助手。
- `./node-websocket`：仅 Node 环境可用的极简 WebSocket 客户端（`connectBinaryWebSocket`、`connectTextWebSocket`）。浏览器环境可直接用全局 `WebSocket`。
- `./session`：仅 Node 环境可用的弹性会话 `createNanaLiveSession`（自动重连、心跳、请求超时）。

## 本地开发

```bash
npm test   # node --test：session 层集成测试（本地 mock WebSocket 服务端）
```
