"""连接 NanaLive，鉴权后打印模型目录。

运行：`python examples/list_models.py`（需要 NanaLive 正在运行；
没有服务端时会报告连接错误）。
"""

import asyncio

from nanalive_sdk import DEFAULT_PORT, connect

IDENTITY = {
    "pluginID": "dev.example.nanalive-python-demo",
    "pluginName": "NanaLive Python Demo",
    "pluginDeveloper": "Example",
    "pluginVersion": "0.1.0",
    "scopes": ["model.read"],
}


async def main() -> None:
    connection = await connect(
        host="127.0.0.1",
        port=DEFAULT_PORT,
        identity=IDENTITY,
        on_token=lambda token: print(f"首次签发的 token（请持久化，下次直接传入）: {token}"),
    )
    try:
        await connection.client.authenticate()
        models = await connection.client.list_models()
        print(f"模型目录: {models}")
    finally:
        await connection.close()


if __name__ == "__main__":
    asyncio.run(main())
