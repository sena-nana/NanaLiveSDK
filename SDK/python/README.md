# nanalive-sdk (Python)

NanaLive 插件 API 的 Python 客户端绑定。连接 NanaLive 的本地控制 API（`ws://127.0.0.1:8312`，子协议 `nanalive-control-v2`，MessagePack 二进制帧），完成鉴权并调用模型、动作、表情、按键和参数接口。

需要 Python ≥ 3.10（asyncio）。

## 安装

```bash
pip install nanalive-sdk                    # 默认使用 websockets（核心依赖）
pip install "nanalive-sdk[aiohttp]"         # 备选传输后端 aiohttp
```

## 传输后端（按常用网络库选择）

传输层可插拔，`connect()` 与 `NanaLiveSession` 均接受 `transport` 参数：

| `transport` | 网络库 | 安装 | 心跳实现 |
|---|---|---|---|
| `"websockets"`（默认） | [websockets](https://pypi.org/project/websockets/) | 核心依赖 | 协议级 ping：`heartbeat_interval` 发 ping，`heartbeat_timeout` 内无 pong 断开 |
| `"aiohttp"` | [aiohttp](https://pypi.org/project/aiohttp/) | `pip install "nanalive-sdk[aiohttp]"` | aiohttp `heartbeat`；pong 判死窗口约为间隔一半（BCL 无独立超时参数） |
| 自定义异步工厂 | 任意 | — | 自行实现 |

自定义后端：传入签名为 `connect(*, host, port, subprotocol, max_size, ping_interval, ping_timeout, open_timeout)` 的异步工厂，返回带 `send`/`close`/异步迭代的适配对象（接口约定见 `nanalive_sdk.transports.base`）。未知名称抛 `NanaLiveError`。

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
    # transport="aiohttp",  # 装了 extra 后切换传输后端
)
await session.connect()  # 含重试；之后的断线由后台任务自动重连
models = await session.request("AvailableModelsRequest")
await session.close()
```

会话可在事件循环外构造（同步上下文先建会话、再进循环连接），但
`connect()`/`request()`/`close()` 必须在其绑定的事件循环内调用；跨线程请用
`asyncio.run_coroutine_threadsafe`。

属性与方法：`client`（底层协议客户端，token 跨重连复用）、`connect()`、
`request(message_type, data)`、`close()`、`status`、`connected`。选项（均有默认值）：
`host`/`port`、`identity`/`token`/`on_token`、`on_unhandled`/`on_error`/`on_status`、
`reconnect`（默认 `True`）、`max_retries`（默认无限）、`retry_delay`/`max_retry_delay`
（0.5s/8s）、`heartbeat_interval`/`heartbeat_timeout`（10s/5s，透传给传输后端）、
`connect_timeout`（5s，`None` 关闭）、`max_frame_size`（16 MiB，入站帧上限）、
`transport`（默认 `"websockets"`）、`request_timeout`（30s，`None` 或 `0` 关闭）。

回调都在保护下调用：`on_status`/`on_unhandled` 抛出的异常经 `on_error` 上报，
不会打断自动重连；重连失败的原因也会经 `on_error` 上报。

会话未连接时 `request` 抛 `NotConnectedError`；超时抛 `RequestTimeoutError`
（迟到的响应会被静默吸收，不影响连接）；断线时挂起请求以 `ConnectionLostError` 失败。

## API 一览

- `connect(...)`（`nanalive_sdk.connection`）：建立 WebSocket 连接，返回
  `NanaLiveConnection`（`.client` + `.transport` + `close()`）。入站 MessagePack
  帧自动喂给客户端，未配对的推送经 `on_unhandled` 回调透传。选项含
  `transport`/`connect_timeout`（`open_timeout`）/`max_size`。
- `nanalive_sdk.transports`：传输后端注册表（`BACKENDS`、`resolve_transport`、
  `TransportClosed`）。
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
