"""可插拔 WebSocket 传输后端。

``transport`` 参数接受内置后端名（``"websockets"`` 默认、``"aiohttp"``
需 extra ``nanalive-sdk[aiohttp]``）或自定义异步工厂，接口见 ``base``。
"""

from __future__ import annotations

from typing import Any, Callable, Dict, Union

from ..api import NanaLiveError
from .aiohttp_backend import connect as connect_aiohttp
from .base import TransportClosed
from .websockets_backend import connect as connect_websockets

TransportFactory = Callable[..., Any]
TransportOption = Union[str, TransportFactory]

BACKENDS: Dict[str, TransportFactory] = {
    "websockets": connect_websockets,
    "aiohttp": connect_aiohttp,
}


def resolve_transport(transport: TransportOption) -> TransportFactory:
    """字符串查注册表，可调用对象（自定义工厂）原样返回。"""
    if callable(transport):
        return transport
    try:
        return BACKENDS[transport]
    except (KeyError, TypeError):
        known = ", ".join(sorted(BACKENDS))
        raise NanaLiveError(
            f"未知传输后端 {transport!r}（可用：{known}，或传自定义异步工厂）"
        ) from None


__all__ = ["BACKENDS", "TransportClosed", "TransportOption", "resolve_transport"]
