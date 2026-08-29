# nanalive-sdk

NanaLive 插件 API 的 Rust 客户端绑定。连接 NanaLive 的本地控制 API（`ws://127.0.0.1:8312`，子协议 `nanalive-control-v2`，MessagePack 二进制帧），完成鉴权并调用模型、动作、表情、按键和参数接口。

需要 Rust 1.75+（异步运行时为 tokio）。

## 安装

```toml
[dependencies]
nanalive-sdk = "0.1"
```

## 用法

```rust
use std::sync::Arc;
use nanalive_sdk::{connect, ConnectOptions, Identity, DEFAULT_PORT};

let handle = connect(ConnectOptions {
    port: DEFAULT_PORT,
    identity: Some(Identity {
        plugin_id: "dev.example.my-plugin".into(),
        plugin_name: "My Plugin".into(),
        plugin_developer: "Example".into(),
        plugin_version: "0.1.0".into(),
        scopes: vec!["model.read".into(), "model.switch".into()],
    }),
    on_token: Some(Arc::new(|token| save_token(token))),
    ..ConnectOptions::new()
})
.await?;

handle.client.authenticate().await?;
let models = handle.client.list_models().await?;
```

`identity` 中的 `plugin_id` 请使用自己的反向域名标识，`scopes` 只申请实际用到的权限；首次申请的 token 经 `on_token` 回调交付，需要用户在 NanaLive 插件页批准，请在本地持久化并在下次连接时作为 `token` 传入。

## 弹性会话（自动重连 + 心跳）

`NanaLiveSession`（`session` 模块，需 `Arc` 持有）在裸连接之上提供完整连接流程：建立 WebSocket → 鉴权 → 心跳保活；断线后挂起中的请求立即失败，按指数退避（带抖动）自动重连并重新鉴权（token 跨重连复用）。

```rust
use std::sync::Arc;
use std::time::Duration;
use nanalive_sdk::{NanaLiveSession, SessionOptions};

let session = Arc::new(NanaLiveSession::new(SessionOptions {
    identity: Some(identity),
    on_token: Some(Arc::new(|token| save_token(token))),
    on_status: Some(Arc::new(|status| println!("{status:?}"))), // Connecting / Connected / Reconnecting / Disconnected
    ..SessionOptions::default()
}));

session.connect().await?; // 含重试；之后的断线由后台任务自动重连
let models = session.request("AvailableModelsRequest", rmpv::Value::Map(vec![])).await?;
session.close().await;
```

方法：`client()`（底层协议客户端，token 跨重连复用）、`connect()`（需 `self: &Arc<Self>`，幂等）、
`request(message_type, data)`、`close()`、`status()`。选项（均有默认值）：`host`/`port`、
`identity`/`token`/`on_token`、`on_unhandled`/`on_error`/`on_status`、`reconnect`（默认 `true`）、
`max_retries`（默认无限）、`retry_delay`/`max_retry_delay`（500ms/8s）、
`heartbeat_interval`/`heartbeat_timeout`（10s/5s，空闲超间隔发 WebSocket ping）、
`request_timeout`（30s，`None` 关闭）。

会话未连接时 `request` 返回 `NanaLiveError::NotConnected`；超时返回 `NanaLiveError::RequestTimeout`；
断线时挂起请求以 `NanaLiveError::ConnectionClosed` 失败。

## API 一览

- [`connect`]（`connection` 模块）：建立 WebSocket 连接并返回 `ConnectionHandle`
  （`client` + 后台泵任务 `task` + `close()`）。入站 MessagePack 帧自动喂给
  客户端，未配对的推送经 `on_unhandled` 回调透传。
- [`NanaLiveClient`]（`client` 模块）：与传输无关的协议客户端，也可
  自行注入 `send` 回调构造：`request(messageType, data)`、`receive(bytes)`、
  `authenticate()`、`list_models / list_motions / list_expressions /
  list_hotkeys / list_parameters`。
- 助手（`helpers` 模块）：`executable_hotkeys`、`parameter_value_after_ticks`、
  `write_parameter_command`。
- 协议常量：`API_NAME`、`API_VERSION`、`SUBPROTOCOL`、`DEFAULT_PORT`。
- 错误：`NanaLiveError`（`Api{message, code}` 对应服务端 `APIError`）。

裸连接（[`connect`]）只负责建立连接与泵任务；自动重连、心跳与请求超时请使用 `NanaLiveSession`。

[`connect`]: https://docs.rs/nanalive-sdk
[`NanaLiveClient`]: https://docs.rs/nanalive-sdk
