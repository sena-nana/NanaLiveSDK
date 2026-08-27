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
  `AuthenticationTokenMissingError`。

SDK 本身不做自动重连与心跳；断线后请自行重建连接并重新 `authenticate()`
（旧 token 仍有效时会直接验证通过）。

## 本地开发

```bash
python -m venv .venv
.venv/Scripts/pip install -e .[dev]   # Windows；macOS/Linux 用 bin/pip
.venv/Scripts/python -m pytest
```
