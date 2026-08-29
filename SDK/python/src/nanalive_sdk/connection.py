"""基于 ``websockets`` 的连接帮手，对应 JS SDK 的 ``connectBinaryWebSocket``
用法（子协议 + 二进制 MessagePack 帧）。"""

from __future__ import annotations

import asyncio
from typing import Any, Callable, Optional

import websockets

from .api import DEFAULT_PORT, NanaLiveClient, SUBPROTOCOL


class NanaLiveConnection:
    """[:meth:`connect`] 的返回值：客户端 + 连接 + 后台泵任务。"""

    def __init__(
        self,
        client: NanaLiveClient,
        websocket,
        outbound: asyncio.Queue,
        tasks: list[asyncio.Task],
    ) -> None:
        self.client = client
        self.websocket = websocket
        self.outbound = outbound
        self._tasks = tasks
        #: 入站泵退出（连接断开）后置位；会话层据此触发重连。
        self.closed = asyncio.Event()

    async def close(self) -> None:
        """优雅关闭连接并等待泵任务退出。"""
        self.closed.set()
        await self.websocket.close()
        for task in self._tasks:
            task.cancel()
        await asyncio.gather(*self._tasks, return_exceptions=True)


async def connect(
    host: str = "127.0.0.1",
    port: int = DEFAULT_PORT,
    identity: Optional[dict] = None,
    token: Optional[str] = None,
    on_token: Optional[Callable[[str], None]] = None,
    on_unhandled: Optional[Callable[[Any], None]] = None,
    on_error: Optional[Callable[[str], None]] = None,
    client: Optional[NanaLiveClient] = None,
    ping_interval: Optional[float] = None,
    ping_timeout: Optional[float] = None,
) -> NanaLiveConnection:
    """连接 NanaLive 控制 API。

    泵任务把入站 MessagePack 帧喂给 :meth:`NanaLiveClient.receive`，
    未配对的推送经 ``on_unhandled`` 回调透传；客户端 ``send`` 的字节经
    出站队列写回 WebSocket。``ping_interval``/``ping_timeout`` 透传给
    ``websockets`` 的协议级心跳（会话层用它做死链检测）；传入
    ``client`` 时复用调用方提供的客户端（会话层跨重连共享 token 与
    等待队列）。
    """
    websocket = await websockets.connect(
        f"ws://{host}:{port}/",
        subprotocols=[SUBPROTOCOL],
        max_size=None,
        ping_interval=ping_interval,
        ping_timeout=ping_timeout,
    )
    loop = asyncio.get_running_loop()
    outbound: asyncio.Queue[bytes] = asyncio.Queue()
    if client is None:
        client = NanaLiveClient(
            send=outbound.put_nowait,
            identity=identity,
            token=token,
            on_token=on_token,
        )

    async def inbound_pump() -> None:
        try:
            async for raw in websocket:
                unhandled = client.receive(raw)
                if unhandled is not None and on_unhandled:
                    on_unhandled(unhandled)
        except websockets.ConnectionClosed:
            pass
        except Exception as exc:  # 协议层异常上报后结束泵任务
            if on_error:
                on_error(f"connection_error: {exc}")
        finally:
            connection.closed.set()

    async def outbound_pump() -> None:
        while True:
            payload = await outbound.get()
            try:
                await websocket.send(payload)
            except websockets.ConnectionClosed:
                break

    tasks = [
        loop.create_task(inbound_pump()),
        loop.create_task(outbound_pump()),
    ]
    # 先绑定局部变量，入站泵的 finally 里要引用 connection.closed。
    connection = NanaLiveConnection(client, websocket, outbound, tasks)
    return connection
