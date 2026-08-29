"""带自动重连、心跳与请求超时的会话层，对应 JS SDK 的 ``session.mjs``。

完整连接流程：建立 WebSocket → 鉴权（优先复用已有 token）→ 心跳保活；
断线后挂起中的请求立即失败，并按指数退避（带抖动）自动重连与重新鉴权。
通过 ``on_status`` 回调观察 ``connecting`` / ``connected`` / ``reconnecting``
/ ``disconnected`` 状态变化。
"""

from __future__ import annotations

import asyncio
import random
from typing import Any, Callable, Optional

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
    传 ``None`` 关闭。
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
        self._on_status and self._on_status(status)

    def _backoff(self, attempt: int) -> float:
        base = min(self._retry_delay * 2 ** (attempt - 1), self._max_retry_delay)
        return max(0.0, base * (1 + 0.2 * (random.random() * 2 - 1)))

    # 建立一次连接并完成鉴权；失败时清理半开连接后向上抛。
    async def _establish(self) -> NanaLiveConnection:
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
        try:
            await self._client.authenticate()
        except Exception:
            self._connection = None
            self._outbound = None
            await _close_quietly(connection)
            raise
        self._set_status(STATUS_CONNECTED)
        return connection

    # 断开后的后台重连循环；close() 取消本任务。
    async def _supervise(self, connection: NanaLiveConnection) -> None:
        while True:
            await connection.closed.wait()
            self._client.fail_pending(ConnectionLostError())
            self._connection = None
            self._outbound = None
            if self._closed:
                return
            if not self._reconnect:
                self._set_status(STATUS_DISCONNECTED)
                return
            attempt = 0
            while True:
                attempt += 1
                if self._max_retries is not None and attempt > self._max_retries:
                    self._set_status(STATUS_DISCONNECTED)
                    return
                self._set_status(STATUS_RECONNECTING)
                await asyncio.sleep(self._backoff(attempt))
                if self._closed:
                    return
                try:
                    connection = await self._establish()
                    break
                except Exception:
                    if self._closed:
                        return

    async def connect(self) -> None:
        """建立会话（内联重试直到首个连接完成鉴权）。

        之后的断线由后台任务自动重连；重复调用会重置会话并重新连接。
        重试耗尽（或 ``reconnect=False`` 且连不上）时抛出最后一次错误。
        """
        if self._supervisor is not None and not self._supervisor.done():
            self._supervisor.cancel()
            await asyncio.gather(self._supervisor, return_exceptions=True)
        self._supervisor = None
        self._closed = False
        if self._connection is not None:
            await _close_quietly(self._connection)
            self._connection = None
        self._outbound = None

        attempt = 0
        while True:
            self._set_status(
                STATUS_CONNECTING if attempt == 0 else STATUS_RECONNECTING
            )
            try:
                connection = await self._establish()
            except Exception as exc:
                attempt += 1
                if (
                    self._closed
                    or not self._reconnect
                    or (self._max_retries is not None and attempt > self._max_retries)
                ):
                    self._set_status(STATUS_DISCONNECTED)
                    raise (
                        exc
                        if isinstance(exc, NanaLiveError)
                        else NanaLiveError(str(exc))
                    ) from None
                await asyncio.sleep(self._backoff(attempt))
                continue
            self._supervisor = asyncio.get_running_loop().create_task(
                self._supervise(connection)
            )
            return

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
