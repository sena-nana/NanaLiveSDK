"""传输后端的归一化接口。

后端工厂签名为 ``connect(*, host, port, subprotocol, max_size,
ping_interval, ping_timeout, open_timeout)``，返回实现下列最小接口的
适配对象：

- ``__aiter__``/``__anext__``：支持 ``async for``，逐条产出入站消息的
  字节，连接结束抛 ``StopAsyncIteration``；
- ``send(payload)``：写出一段字节，连接已断时抛 :class:`TransportClosed`；
- ``close()``：优雅关闭并释放底层资源，可安全重复调用。
"""

from __future__ import annotations


class TransportClosed(Exception):
    """连接已关闭。

    对 ``websockets.ConnectionClosed`` 等各后端关闭异常的统一抽象。
    """
