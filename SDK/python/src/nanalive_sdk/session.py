"""带自动重连、心跳与请求超时的会话层，对应 JS SDK 的 ``session.mjs``。

完整连接流程：建立 WebSocket → 鉴权（优先复用已有 token）→ 心跳保活；
断线后挂起中的请求立即失败，并按指数退避（带抖动）自动重连与重新鉴权。
通过 ``on_status`` 回调观察 ``connecting`` / ``connected`` / ``reconnecting``
/ ``disconnected`` 状态变化。
"""

from __future__ import annotations

import asyncio
import random
from typing import Any, Callable, List, Optional

from .api import (
    DEFAULT_PORT,
    ConnectionLostError,
    NanaLiveClient,
    NanaLiveError,
    NotConnectedError,
    RequestTimeoutError,
    _UNSET,
)
from .connection import NanaLiveConnection, connect

STATUS_CONNECTING = "connecting"
STATUS_CONNECTED = "connected"
STATUS_RECONNECTING = "reconnecting"
STATUS_DISCONNECTED = "disconnected"

StatusCallback = Callable[[str], None]


class NanaLiveSession:
    """NanaLive 插件 API 的弹性会话。

    心跳交给 ``websockets`` 的协议级 ping：每 ``heartbeat_interval`` 秒发
    ping，``heartbeat_timeout`` 秒内没收到 pong 就断开并触发重连。
    ``max_retries`` 为 ``None`` 时无限重试；``request_timeout`` 默认 30 秒，
    传 ``None`` 关闭单请求超时。
    """

    def __init__(
        self,
        *,
        host: str = "127.0.0.1",
        port: int = DEFAULT_PORT,
        identity: Optional[dict] = None,
        token: Optional[str] = None,
        on_token: Optional[Callable[[str], None]] = None,
        on_unhandled: Optional[Callable[[Any], None]] = None,
        on_error: Optional[Callable[[str], None]] = None,
        on_status: Optional[StatusCallback] = None,
        reconnect: bool = True,
        max_retries: Optional[int] = None,
        retry_delay: float = 0.5,
        max_retry_delay: float = 8.0,
        heartbeat_interval: float = 10.0,
        heartbeat_timeout: float = 5.0,
        request_timeout: Optional[float] = 30.0,
    ) -> None:
        if heartbeat_interval <= 0:
            raise ValueError("heartbeat_interval 必须为正数")
        if retry_delay <= 0 or max_retry_delay < retry_delay:
            raise ValueError("retry_delay/max_retry_delay 配置无效")
        self.host = host
        self.port = port
        self._on_unhandled = on_unhandled
        self._on_error = on_error
        self._on_status = on_status
        self._reconnect = reconnect
        self._max_retries = max_retries
        self._retry_delay = retry_delay
        self._max_retry_delay = max_retry_delay
        self._heartbeat_interval = heartbeat_interval
        self._heartbeat_timeout = heartbeat_timeout
        self._request_timeout = request_timeout

        self._outbound: Optional[asyncio.Queue] = None
        self._connection: Optional[NanaLiveConnection] = None
        self._client = NanaLiveClient(
            send=self._route,
            identity=identity,
            token=token,
            on_token=on_token,
        )
        self._status = STATUS_DISCONNECTED
        self._closed = False
        self._supervisor: Optional[asyncio.Task] = None
        self._attempt = 0
        self._connect_waiters: List[asyncio.Future] = []

    @property
    def client(self) -> NanaLiveClient:
        """底层协议客户端（token 在多次重连之间保持复用）。"""
        return self._client

    @property
    def status(self) -> str:
        return self._status

    @property
    def connected(self) -> bool:
        return self._status == STATUS_CONNECTED and self._connection is not None

    def _route(self, payload: bytes) -> None:
        if self._outbound is None:
            raise NotConnectedError()
        self._outbound.put_nowait(payload)

    def _set_status(self, status: str) -> None:
        if self._status == status:
            return
        self._status = status
        if status == STATUS_CONNECTED:
            for waiter in self._take_connect_waiters():
                waiter.set_result(None)
        self._on_status and self._on_status(status)

    def _take_connect_waiters(self) -> List[asyncio.Future]:
        waiters, self._connect_waiters = self._connect_waiters, []
        return waiters

    def _fail_connect_waiters(self, error: NanaLiveError) -> None:
        for waiter in self._take_connect_waiters():
            if not waiter.done():
                waiter.set_exception(type(error)())

    def _backoff(self) -> float:
        base = min(self._retry_delay * 2**self._attempt, self._max_retry_delay)
        return max(0.0, base * (1 + 0.2 * (random.random() * 2 - 1)))

    async def connect(self) -> None:
        """建立会话（含重试），首个连接完成鉴权后返回。

        之后的断线由后台任务自动重连；重复调用是幂等的。
        重试耗尽（或 ``reconnect=False`` 且连不上）时抛出最后一次错误。
        """
        if self._supervisor is None or self._supervisor.done():
            self._closed = False
            self._supervisor = asyncio.get_running_loop().create_task(self._run())
        if self._status == STATUS_CONNECTED:
            return
        waiter: asyncio.Future = asyncio.get_running_loop().create_future()
        self._connect_waiters.append(waiter)
        await waiter

    async def _run(self) -> None:
        failure: Optional[NanaLiveError] = None
        while not self._closed:
            self._set_status(
                STATUS_CONNECTING if self._attempt == 0 else STATUS_RECONNECTING
            )
            connection: Optional[NanaLiveConnection] = None
            try:
                connection = await connect(
                    host=self.host,
                    port=self.port,
                    on_unhandled=self._on_unhandled,
                    on_error=self._on_error,
                    client=self._client,
                    ping_interval=self._heartbeat_interval,
                    ping_timeout=self._heartbeat_timeout,
                )
                self._connection = connection
                self._outbound = connection.outbound
                await self._client.authenticate()
                self._attempt = 0
                failure = None
                self._set_status(STATUS_CONNECTED)
                await connection.closed.wait()
                self._client.fail_pending(ConnectionLostError())
            except Exception as exc:
                failure = (
                    exc if isinstance(exc, NanaLiveError) else NanaLiveError(str(exc))
                )
                self._outbound = None
                self._connection = None
                if connection is not None:
                    await _close_quietly(connection)

            if self._closed:
                break
            if not self._reconnect:
                self._set_status(STATUS_DISCONNECTED)
                self._fail_connect_waiters(failure or ConnectionLostError())
                break
            self._attempt += 1
            if (
                self._max_retries is not None
                and self._attempt > self._max_retries
            ):
                self._set_status(STATUS_DISCONNECTED)
                self._fail_connect_waiters(failure or ConnectionLostError())
                break
            await asyncio.sleep(self._backoff())

    async def request(
        self,
        message_type: str,
        data: Any = _UNSET,
    ) -> Any:
        """发送一条请求并等待配对的响应；断线时立刻失败。

        会话未连接时抛 :class:`NotConnectedError`；超过会话级的
        ``request_timeout`` 抛 :class:`RequestTimeoutError`。
        """
        if self._connection is None or self._connection.closed.is_set():
            raise NotConnectedError()
        if not self._request_timeout:
            return await self._client.request(message_type, data)
        try:
            return await asyncio.wait_for(
                self._client.request(message_type, data), self._request_timeout
            )
        except asyncio.TimeoutError:
            raise RequestTimeoutError() from None

    async def close(self) -> None:
        """停止重连并关闭底层连接；挂起中的请求立即失败。"""
        if self._closed:
            return
        self._closed = True
        self._fail_connect_waiters(NanaLiveError("closed"))
        if self._supervisor is not None:
            self._supervisor.cancel()
            await asyncio.gather(self._supervisor, return_exceptions=True)
            self._supervisor = None
        if self._connection is not None:
            await _close_quietly(self._connection)
            self._connection = None
        self._outbound = None
        self._client.fail_pending(ConnectionLostError())
        self._set_status(STATUS_DISCONNECTED)


async def _close_quietly(connection: NanaLiveConnection) -> None:
    try:
        await connection.close()
    except Exception:
        pass
