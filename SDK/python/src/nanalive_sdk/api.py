"""NanaLive 插件 API 的协议客户端，对应 JS SDK 的 ``api.mjs``。"""

from __future__ import annotations

import asyncio
import math
from typing import Any, Callable, Dict, List, Optional

import msgpack

API_NAME = "NanaLiveControlAPI"
API_VERSION = "2.0"
SUBPROTOCOL = "nanalive-control-v2"
DEFAULT_PORT = 8312

#: 全量程对应的刻度数（与 JS SDK 一致）。
FULL_RANGE_TICKS = 40.0

#: 全量程对应的刻度数（与 JS SDK 一致）。
FULL_RANGE_TICKS = 40.0

_UNSET = object()


class NanaLiveError(Exception):
    """服务端返回 ``messageType == "APIError"`` 时抛出。"""

    def __init__(self, message: str, code: Any = None) -> None:
        super().__init__(message)
        self.code = code


class AuthenticationTokenMissingError(NanaLiveError):
    """鉴权时服务端没有签发 token。"""

    def __init__(self) -> None:
        super().__init__("authentication_token_missing")


class NotConnectedError(NanaLiveError):
    """会话未连接时发起请求。"""

    def __init__(self) -> None:
        super().__init__("not_connected")


class ConnectionLostError(NanaLiveError):
    """连接在请求等待期间断开。"""

    def __init__(self) -> None:
        super().__init__("connection_lost")


class RequestTimeoutError(NanaLiveError):
    """请求在超时时间内没有等到响应。"""

    def __init__(self) -> None:
        super().__init__("request_timeout")


def _number(value: Any) -> float:
    """按 JS ``Number`` 的宽松语义转浮点，失败得 NaN。"""
    if value is None:
        return math.nan
    if isinstance(value, bool):
        return float(value)
    if isinstance(value, (int, float)):
        return float(value)
    if isinstance(value, str):
        try:
            return float(value)
        except ValueError:
            return math.nan
    return math.nan


def executable_hotkeys(hotkeys: Optional[List[Any]] = None) -> List[Any]:
    """从按键目录中过滤出可执行的按键（``executable is True``）。"""
    return [
        hotkey
        for hotkey in (hotkeys or [])
        if isinstance(hotkey, dict) and hotkey.get("executable") is True
    ]


def parameter_value_after_ticks(parameter: Optional[Dict[str, Any]], ticks: float) -> float:
    """参数当前值按旋钮刻度推算后的目标值。

    每 40 刻度走完全量程，并钳制在 ``min``/``max`` 之内；无效输入的
    回退行为与 JS 版一致。
    """
    if not parameter:
        return 0.0
    value = _number(parameter.get("value"))
    low = _number(parameter.get("min"))
    high = _number(parameter.get("max"))
    if not math.isfinite(ticks) or ticks == 0:
        return value if math.isfinite(value) else 0.0
    span = high - low
    step = 1.0 if span == 0 or not math.isfinite(span) else span / FULL_RANGE_TICKS
    target = value + ticks * step
    if not math.isfinite(target):
        return value
    # 与 JS 的 Math.min(max, Math.max(min, next)) 一致。
    return min(high, max(low, target))


def write_parameter_command(parameter_id: Optional[str], value: float) -> Optional[Dict[str, Any]]:
    """构造写入单个参数值的 ``ParameterWriteRequest`` 命令。

    ``parameter_id`` 为空或 ``value`` 非有限时返回 ``None``。
    """
    if not parameter_id or not math.isfinite(value):
        return None
    return {
        "messageType": "ParameterWriteRequest",
        "data": {"parameters": {parameter_id: value}},
    }


class NanaLiveClient:
    """NanaLive 插件 API 客户端。

    与传输解耦：构造时注入同步的 ``send`` 回调负责把编码后的字节写出
    去，收到字节后调用 :meth:`receive` 喂回客户端即可。
    """

    def __init__(
        self,
        send: Callable[[bytes], None],
        identity: Optional[Dict[str, Any]] = None,
        token: Optional[str] = None,
        on_token: Optional[Callable[[str], None]] = None,
    ) -> None:
        if not callable(send):
            raise TypeError("send is required")
        self._send = send
        self._identity = identity
        self._token = token
        self._on_token = on_token
        self._waiters: Dict[str, asyncio.Future] = {}
        self._sequence = 0
        self._loop = asyncio.get_running_loop()

    async def request(
        self, message_type: str, data: Any = _UNSET
    ) -> Dict[str, Any]:
        """发送一条请求并等待配对的响应。"""
        self._sequence += 1
        request_id = f"nanalive-{self._sequence}"
        future: asyncio.Future = self._loop.create_future()
        self._waiters[request_id] = future
        envelope = {
            "apiName": API_NAME,
            "apiVersion": API_VERSION,
            "requestID": request_id,
            "messageType": message_type,
            "data": {} if data is _UNSET else data,
        }
        try:
            self._send(msgpack.packb(envelope, use_bin_type=True))
        except Exception:
            self._waiters.pop(request_id, None)
            raise
        return await future

    def receive(self, raw: Any) -> Optional[Any]:
        """把一段收到的消息喂回客户端。

        ``raw`` 可以是 MessagePack 字节或已解码的响应对象。返回 ``None``
        表示响应已配对给等待中的请求；返回非 ``None`` 表示没有匹配的
        等待者（服务器主动推送），原样透传给调用方。
        """
        response = (
            msgpack.unpackb(raw, raw=False)
            if isinstance(raw, (bytes, bytearray, memoryview))
            else raw
        )
        request_id = response.get("requestID") if isinstance(response, dict) else None
        future = self._waiters.pop(request_id, None) if request_id is not None else None
        if future is None:
            return response
        if isinstance(response, dict) and response.get("messageType") == "APIError":
            data = response.get("data") or {}
            future.set_exception(
                NanaLiveError(data.get("message") or "api_error", data.get("errorCode"))
            )
        else:
            future.set_result(response)
        return None

    def fail_pending(self, error: Optional[NanaLiveError] = None) -> int:
        """让所有等待中的请求立即失败（连接断开时由会话层调用）。

        返回清掉的等待者数量。
        """
        waiters = list(self._waiters.values())
        self._waiters.clear()
        failure = error or ConnectionLostError()
        for future in waiters:
            if not future.done():
                future.set_exception(failure)
        return len(waiters)

    async def authenticate(self) -> Dict[str, Any]:
        """两段式鉴权：已有 token 先尝试验证，失败降级为申请新 token。"""
        if self._token:
            try:
                return await self.request(
                    "AuthenticationRequest", {"authenticationToken": self._token}
                )
            except Exception:
                self._token = None

        issued = await self.request("AuthenticationTokenRequest", self._identity)
        token = (issued.get("data") or {}).get("authenticationToken")
        if not token:
            raise AuthenticationTokenMissingError()
        self._token = token
        if self._on_token:
            self._on_token(token)
        return await self.request(
            "AuthenticationRequest", {"authenticationToken": self._token}
        )

    async def list_models(self) -> Dict[str, Any]:
        """``AvailableModelsRequest``。"""
        return await self.request("AvailableModelsRequest")

    async def list_motions(self) -> Dict[str, Any]:
        """``MotionListRequest``。"""
        return await self.request("MotionListRequest")

    async def list_expressions(self) -> Dict[str, Any]:
        """``ExpressionListRequest``。"""
        return await self.request("ExpressionListRequest")

    async def list_hotkeys(self) -> Dict[str, Any]:
        """``HotkeyListRequest``。"""
        return await self.request("HotkeyListRequest")

    async def list_parameters(self) -> Dict[str, Any]:
        """``ParameterListRequest``。"""
        return await self.request("ParameterListRequest")
