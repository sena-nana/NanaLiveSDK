"""基于 ``aiohttp`` 的传输后端（需要 extra：``pip install nanalive-sdk[aiohttp]``）。

心跳映射：``ping_interval`` 传给 aiohttp 的 ``heartbeat``；aiohttp 的
pong 判死窗口是其内部实现（约间隔一半），``ping_timeout`` 不生效。
"""

from __future__ import annotations

from typing import Optional

from ..api import NanaLiveError
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
    try:
        import aiohttp
    except ImportError:
        raise NanaLiveError(
            "aiohttp 传输后端未安装：请执行 pip install nanalive-sdk[aiohttp]"
        ) from None

    kwargs: dict = {
        "protocols": (subprotocol,),
        "max_msg_size": max_size,
        "autoping": True,
    }
    if ping_interval is not None and ping_interval > 0:
        kwargs["heartbeat"] = ping_interval

    # 建链+握手超时经会话级 ClientTimeout.total 实现（aiohttp 3.11 起移除
    # 了独立的 ws_open 参数）；握手完成后该超时不再约束消息收发。
    session_kwargs: dict = {}
    if open_timeout is not None:
        session_kwargs["timeout"] = aiohttp.ClientTimeout(total=open_timeout)

    session = aiohttp.ClientSession(**session_kwargs)
    try:
        ws = await session.ws_connect(f"ws://{host}:{port}/", **kwargs)
    except BaseException:
        await session.close()
        raise
    return AiohttpTransport(aiohttp, ws, session)


class AiohttpTransport:
    """包装 aiohttp 的 ws 连接与所属 ClientSession（随 close 一并释放）。"""

    def __init__(self, aiohttp_module, ws, session) -> None:
        self._aiohttp = aiohttp_module
        self._ws = ws
        self._session = session

    async def send(self, payload: bytes) -> None:
        try:
            await self._ws.send_bytes(payload)
        except Exception as exc:
            if self._ws.closed:
                raise TransportClosed(str(exc)) from exc
            raise

    async def close(self) -> None:
        try:
            await self._ws.close()
        finally:
            await self._session.close()

    def __aiter__(self):
        return self

    async def __anext__(self) -> bytes:
        msg = await self._ws.receive()
        if msg.type == self._aiohttp.WSMsgType.BINARY:
            return msg.data
        if msg.type == self._aiohttp.WSMsgType.TEXT:
            # 协议约定二进制帧；文本帧视为流错位，断开重连。
            raise TransportClosed("unexpected text frame")
        raise StopAsyncIteration from None
