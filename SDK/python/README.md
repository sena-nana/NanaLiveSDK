# nanalive-sdk (Python)

NanaLive 插件 API 的 Python 客户端绑定。连接 NanaLive 的本地控制 API（`ws://127.0.0.1:8312`，子协议 `nanalive-control-v2`，MessagePack 二进制帧），完成鉴权并调用模型、动作、表情、按键和参数接口。

需要 Python ≥ 3.10（asyncio）。

## 安装

```bash
pip install nanalive-sdk
```

## 用法

```python
import asyncio
from nanalive_sdk import connect, DEFAULT_PORT

async def main():
    connection = await connect(
        port=DEFAULT_PORT,
        identity={
            "pluginID": "dev.example.my-plugin",
            "pluginName": "My Plugin",
            "pluginDeveloper": "Example",
            "pluginVersion": "0.1.0",
            "scopes": ["model.read", "model.switch"],
        },
        on_token=lambda token: save_token(token),
    )
    try:
        await connection.client.authenticate()
        models = await connection.client.list_models()
    finally:
        await connection.close()

asyncio.run(main())
```

`identity` 中的 `pluginID` 请使用自己的反向域名标识，`scopes` 只申请实际用到的权限；首次申请的 token 经 `on_token` 回调交付，需要用户在 NanaLive 插件页批准，请在本地持久化并在下次连接时作为 `token` 参数传入。

## 弹性会话（自动重连 + 心跳）

`NanaLiveSession`（`nanalive_sdk.session`）在裸连接之上提供完整连接流程：建立 WebSocket → 鉴权 → 心跳保活；断线后挂起中的请求立即失败，按指数退避（带抖动）自动重连并重新鉴权（token 跨重连复用）。

```python
from nanalive_sdk import NanaLiveSession, DEFAULT_PORT

session = NanaLiveSession(
    port=DEFAULT_PORT,
    identity={...},
    on_token=save_token,
    on_status=lambda status: print(status),  # connecting / connected / reconnecting / disconnected
)
await session.connect()  # 含重试；之后的断线由后台任务自动重连
models = await session.request("AvailableModelsRequest")
await session.close()
```

属性与方法：`client`（底层协议客户端，token 跨重连复用）、`connect()`（幂等）、
`request(message_type, data)`、`close()`、`status`、`connected`。选项（均有默认值）：
`host`/`port`、`identity`/`token`/`on_token`、`on_unhandled`/`on_error`/`on_status`、
`reconnect`（默认 `True`）、`max_retries`（默认无限）、`retry_delay`/`max_retry_delay`
（0.5s/8s）、`heartbeat_interval`/`heartbeat_timeout`（10s/5s，透传给 `websockets`
的协议级 ping）、`request_timeout`（30s，`None` 关闭）。

会话未连接时 `request` 抛 `NotConnectedError`；超时抛 `RequestTimeoutError`；
断线时挂起请求以 `ConnectionLostError` 失败。

## API 一览

- `connect(...)`（`nanalive_sdk.connection`）：建立 WebSocket 连接，返回
  `NanaLiveConnection`（`.client` + `close()`）。入站 MessagePack 帧自动喂给
  客户端，未配对的推送经 `on_unhandled` 回调透传。
- `NanaLiveClient`（`nanalive_sdk.api`）：与传输无关的协议客户端，也可
  自行注入 `send` 回调构造：`await request(message_type, data)`、
  `receive(raw)`、`await authenticate()`、`await list_models() /
  list_motions() / list_expressions() / list_hotkeys() / list_parameters()`。
- 助手：`executable_hotkeys`、`parameter_value_after_ticks`、
  `write_parameter_command`。
- 协议常量：`API_NAME`、`API_VERSION`、`SUBPROTOCOL`、`DEFAULT_PORT`。
- 异常：`NanaLiveError`（`.code` 对应服务端 `errorCode`）及其子类
  `AuthenticationTokenMissingError`、`NotConnectedError`、`ConnectionLostError`、
  `RequestTimeoutError`。

裸连接（`connect(...)`）只负责建立连接与泵任务；自动重连、心跳与请求超时请使用 `NanaLiveSession`。

## 本地开发

```bash
python -m venv .venv
.venv/Scripts/pip install -e .[dev]   # Windows；macOS/Linux 用 bin/pip
.venv/Scripts/python -m pytest
```
