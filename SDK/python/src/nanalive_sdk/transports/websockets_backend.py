"""基于 ``websockets`` 库的传输后端（默认，核心依赖）。

心跳直接使用 ``websockets`` 的协议级 ping：``ping_interval`` 为发送
间隔，``ping_timeout`` 内未收到 pong 即断开。
"""

from __future__ import annotations

from typing import Optional

import websockets

from .base import TransportClosed


async def connect(
    *,
    host: str,
    port: int,
    subprotocol: str,
    max_size: Optional[int] = None,
    ping_interval: Optional[float] = None,
    ping_timeout: Optional[float] = None,
    open_timeout: Optional[float] = None,
):
    websocket = await websockets.connect(
        f"ws://{host}:{port}/",
        subprotocols=[subprotocol],
        max_size=max_size,
        ping_interval=ping_interval,
        ping_timeout=ping_timeout,
        open_timeout=open_timeout,
    )
    return WebsocketsTransport(websocket)


class WebsocketsTransport:
    """包装 websockets 连接，归一化 send/close/迭代的错误面。"""

    def __init__(self, websocket) -> None:
        self._ws = websocket

    async def send(self, payload: bytes) -> None:
        try:
            await self._ws.send(payload)
        except websockets.ConnectionClosed as exc:
            raise TransportClosed(str(exc)) from exc

    async def close(self) -> None:
        await self._ws.close()

    def __aiter__(self):
        return self

    async def __anext__(self) -> bytes:
        # 新旧实现都没有可直接 await 的 __anext__，用 recv() 收消息。
        try:
            return await self._ws.recv()
        except websockets.ConnectionClosed:
            raise StopAsyncIteration from None
