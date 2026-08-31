"""弹性会话示例：自动重连、心跳、请求超时与传输后端切换。

运行：`python examples/session_resilient.py`（需要 NanaLive 正在运行；
没有服务端时会看到 connecting/reconnecting 状态循环）。

切换网络库：`pip install "nanalive-sdk[aiohttp]"` 后把 TRANSPORT 改为
"aiohttp" 即可；默认使用核心依赖 websockets。
"""

import asyncio

from nanalive_sdk import DEFAULT_PORT, NanaLiveSession

#: 可选值："websockets"（默认）、"aiohttp"（需 extra），或自定义异步工厂。
TRANSPORT = "websockets"

IDENTITY = {
    "pluginID": "dev.example.nanalive-python-demo",
    "pluginName": "NanaLive Python Demo",
    "pluginDeveloper": "Example",
    "pluginVersion": "0.1.0",
    "scopes": ["model.read"],
}


async def main() -> None:
    session = NanaLiveSession(
        host="127.0.0.1",
        port=DEFAULT_PORT,
        identity=IDENTITY,
        on_token=lambda token: print(f"首次签发的 token（请持久化）: {token}"),
        on_status=lambda status: print(f"状态: {status}"),
        on_error=lambda message: print(f"错误: {message}"),
        transport=TRANSPORT,
    )
    try:
        await session.connect()
        models = await session.request("AvailableModelsRequest")
        print(f"模型目录: {models['data']['models']}")
    finally:
        await session.close()


if __name__ == "__main__":
    asyncio.run(main())
