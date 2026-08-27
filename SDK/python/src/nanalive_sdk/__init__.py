"""NanaLive 插件 API 的 Python 客户端绑定。

连接 NanaLive 的本地控制 API（``ws://127.0.0.1:8312``，子协议
``nanalive-control-v2``，MessagePack 二进制帧），完成鉴权并调用模型、
动作、表情、按键和参数接口。
"""

from .api import (
    API_NAME,
    API_VERSION,
    DEFAULT_PORT,
    SUBPROTOCOL,
    AuthenticationTokenMissingError,
    NanaLiveClient,
    NanaLiveError,
    executable_hotkeys,
    parameter_value_after_ticks,
    write_parameter_command,
)
from .connection import NanaLiveConnection, connect

__all__ = [
    "API_NAME",
    "API_VERSION",
    "DEFAULT_PORT",
    "SUBPROTOCOL",
    "AuthenticationTokenMissingError",
    "NanaLiveClient",
    "NanaLiveConnection",
    "NanaLiveError",
    "connect",
    "executable_hotkeys",
    "parameter_value_after_ticks",
    "write_parameter_command",
]
