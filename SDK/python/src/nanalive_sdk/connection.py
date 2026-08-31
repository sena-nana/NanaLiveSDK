"""连接帮手：经可插拔传输后端建立 WebSocket 并驱动收发泵。

对应 JS SDK 的 ``connectBinaryWebSocket`` 用法（子协议 + 二进制
MessagePack 帧）。传输后端由 ``transport`` 参数选择，详见
:mod:`nanalive_sdk.transports`。
"""

from __future__ import annotations

import asyncio
from typing import Any, Callable, Optional

from .api import DEFAULT_PORT, NanaLiveClient, SUBPROTOCOL
from .transports import TransportClosed, TransportOption, resolve_transport

#: 入站帧默认大小上限：超过即断开（防异常服务端把内存吃光）。
DEFAULT_MAX_FRAME_SIZE = 16 * 1024 * 1024


def _report(on_error: Optional[Callable[[str], None]], message: str) -> None:
    """上报错误；回调自身抛异常也不外泄（不能反过来杀死泵任务）。"""
    if on_error is None:
        return
    try:
        on_error(message)
    except Exception:
        pass


class NanaLiveConnection:
    """[:meth:`connect`] 的返回值：客户端 + 连接 + 后台泵任务。"""

    def __init__(
        self,
        client: NanaLiveClient,
        transport,
        outbound: asyncio.Queue,
        tasks: list[asyncio.Task],
    ) -> None:
        self.client = client
        #: 传输适配对象（默认后端下包装 websockets 连接）。
        self.transport = transport
        self.outbound = outbound
        self._tasks = tasks
        #: 入站泵退出（连接断开）后置位；会话层据此触发重连。
        self.closed = asyncio.Event()

    async def close(self) -> None:
        """优雅关闭连接并等待泵任务退出；可安全重复调用。"""
        self.closed.set()
        try:
            await self.transport.close()
        except Exception:
            pass
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
    open_timeout: Optional[float] = None,
    max_size: Optional[int] = DEFAULT_MAX_FRAME_SIZE,
    transport: TransportOption = "websockets",
) -> NanaLiveConnection:
    """连接 NanaLive 控制 API。

    泵任务把入站 MessagePack 帧喂给 :meth:`NanaLiveClient.receive`，
    未配对的推送经 ``on_unhandled`` 回调透传；客户端 ``send`` 的字节经
    出站队列写回 WebSocket。``ping_interval``/``ping_timeout`` 是心跳
    参数（语义随传输后端不同，会话层用它做死链检测）；``open_timeout``
    限制建链+握手的最长时间；``transport`` 选择传输后端（内置
    ``"websockets"``/``"aiohttp"``，或传自定义异步工厂）。传入
    ``client`` 时复用调用方提供的客户端（会话层跨重连共享 token 与
    等待队列）。
    """
    factory = resolve_transport(transport)
    websocket = await factory(
        host=host,
        port=port,
        subprotocol=SUBPROTOCOL,
        max_size=max_size,
        ping_interval=ping_interval,
        ping_timeout=ping_timeout,
        open_timeout=open_timeout,
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
                if unhandled is not None and on_unhandled is not None:
                    try:
                        on_unhandled(unhandled)
                    except Exception as exc:
                        _report(on_error, f"on_unhandled callback error: {exc}")
        except Exception as exc:  # 关闭走 StopAsyncIteration；其余（含解码错位）上报后结束泵任务
            _report(on_error, f"connection_error: {exc}")
        finally:
            connection.closed.set()

    async def outbound_pump() -> None:
        while True:
            payload = await outbound.get()
            try:
                await websocket.send(payload)
            except TransportClosed:
                break
            except Exception as exc:
                # 连接意外出错：上报后退队失败（消息随断线语义丢弃）。
                _report(on_error, f"connection_error: {exc}")
                break

    tasks = [
        loop.create_task(inbound_pump()),
        loop.create_task(outbound_pump()),
    ]
    # 先绑定局部变量，入站泵的 finally 里要引用 connection.closed。
    connection = NanaLiveConnection(client, websocket, outbound, tasks)
    return connection
