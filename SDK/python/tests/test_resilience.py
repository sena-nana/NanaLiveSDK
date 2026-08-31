"""会话层韧性回归测试：修复过的竞态、泄漏与畸形数据处理。"""

import asyncio

import msgpack
import pytest
import websockets

from nanalive_sdk import (
    API_NAME,
    API_VERSION,
    SUBPROTOCOL,
    ConnectionLostError,
    NanaLiveError,
    NanaLiveSession,
    RequestTimeoutError,
    connect,
)
from nanalive_sdk.transports import TransportClosed, resolve_transport


def encode(value) -> bytes:
    return msgpack.packb(value, use_bin_type=True)


def envelope(request_id, message_type, data):
    return {
        "apiName": API_NAME,
        "apiVersion": API_VERSION,
        "requestID": request_id,
        "messageType": message_type,
        "data": data,
    }


def route(request):
    request_id = request["requestID"]
    message_type = request["messageType"]
    if message_type == "AuthenticationTokenRequest":
        return envelope(request_id, "AuthenticationTokenResponse", {"authenticationToken": "issued-token"})
    if message_type == "AuthenticationRequest":
        return envelope(request_id, "AuthenticationResponse", {})
    if message_type == "AvailableModelsRequest":
        return envelope(request_id, "AvailableModelsResponse", {"models": [{"modelID": "m-1"}]})
    return None


IDENTITY = {
    "pluginID": "dev.example.plugin",
    "pluginName": "Example",
    "pluginDeveloper": "Example",
    "pluginVersion": "0.1.0",
    "scopes": ["model.read"],
}


async def wait_for(predicate, timeout=5.0, interval=0.05):
    deadline = asyncio.get_running_loop().time() + timeout
    while asyncio.get_running_loop().time() < deadline:
        if await predicate() if callable(predicate) else predicate:
            return True
        await asyncio.sleep(interval)
    return False


def pump_tasks() -> list:
    """当前事件循环里存活的收发泵任务。"""
    return [
        task
        for task in asyncio.all_tasks()
        if "inbound_pump" in repr(task.get_coro()) or "outbound_pump" in repr(task.get_coro())
    ]


async def test_late_response_after_timeout_is_absorbed():
    """请求超时后迟到的响应不应杀死入站泵（假断线重连）。"""
    connections = 0
    gate = asyncio.Event()

    async def handler(websocket):
        nonlocal connections
        connections += 1
        delayed = True
        async for raw in websocket:
            request = msgpack.unpackb(raw, raw=False)
            if request["messageType"] == "AvailableModelsRequest" and delayed:
                delayed = False
                await gate.wait()  # 拖过客户端的请求超时
            response = route(request)
            if response is not None:
                await websocket.send(encode(response))

    async with websockets.serve(handler, "127.0.0.1", 0, subprotocols=[SUBPROTOCOL]) as server:
        port = server.sockets[0].getsockname()[1]
        session = NanaLiveSession(port=port, identity=IDENTITY, request_timeout=0.2)
        await session.connect()
        with pytest.raises(RequestTimeoutError):
            await session.request("AvailableModelsRequest")

        # 放行服务端，把迟到的响应发出来；入站泵应静默吸收。
        gate.set()
        await asyncio.sleep(0.3)
        assert connections == 1, "迟到响应不应触发重连"
        assert session.connected

        # 同一条连接上的后续请求正常工作。
        models = await session.request("AvailableModelsRequest")
        assert models["data"]["models"] == [{"modelID": "m-1"}]
        assert connections == 1
        await session.close()


async def test_malformed_api_error_data_does_not_kill_connection():
    """``APIError.data`` 为非字典时按 api_error 处理，连接保持可用。"""
    errors = []

    async def handler(websocket):
        async for raw in websocket:
            request = msgpack.unpackb(raw, raw=False)
            if request["messageType"] == "AvailableModelsRequest":
                await websocket.send(encode(envelope(request["requestID"], "APIError", "boom")))
                continue
            response = route(request)
            if response is not None:
                await websocket.send(encode(response))

    async with websockets.serve(handler, "127.0.0.1", 0, subprotocols=[SUBPROTOCOL]) as server:
        port = server.sockets[0].getsockname()[1]
        session = NanaLiveSession(port=port, identity=IDENTITY, on_error=errors.append)
        await session.connect()
        with pytest.raises(NanaLiveError, match="api_error"):
            await session.request("AvailableModelsRequest")
        assert session.connected, "畸形错误消息不应断开连接"
        assert errors == []
        await session.close()


def test_session_constructible_outside_event_loop():
    """会话可在事件循环外构造（同步上下文先建会话、再进循环连接）。"""
    session = NanaLiveSession(port=1)
    assert session.status == "disconnected"


async def test_no_pump_leak_after_reconnect():
    """每次断线重连不应泄漏出站泵任务。"""

    async def handler(websocket):
        async for raw in websocket:
            request = msgpack.unpackb(raw, raw=False)
            if request["messageType"] == "DropConnectionRequest":
                await websocket.close(code=1011)
                return
            response = route(request)
            if response is not None:
                await websocket.send(encode(response))

    async with websockets.serve(handler, "127.0.0.1", 0, subprotocols=[SUBPROTOCOL]) as server:
        port = server.sockets[0].getsockname()[1]
        session = NanaLiveSession(
            port=port, identity=IDENTITY, retry_delay=0.05, max_retry_delay=0.1
        )
        await session.connect()
        assert len(pump_tasks()) == 2

        # 服务端直接断开，挂起请求立即失败，会话自动重连。
        with pytest.raises(ConnectionLostError):
            await session.request("DropConnectionRequest")

        async def reconnected():
            try:
                await session.request("AvailableModelsRequest")
                return True
            except Exception:
                return False

        assert await wait_for(reconnected), "重连后未能完成请求"
        await asyncio.sleep(0.2)  # 留给旧泵任务退出
        assert len(pump_tasks()) == 2, f"重连后泵任务泄漏：{pump_tasks()}"
        await session.close()
        await asyncio.sleep(0.2)
        assert len(pump_tasks()) == 0, "close() 后泵任务应全部退出"


async def test_close_during_connect_does_not_leave_zombie_session():
    """close() 与建连竞态：close 返回后会话不得再回到 connected。"""
    connection_count = 0

    async def handler(websocket):
        nonlocal connection_count
        connection_count += 1
        async for raw in websocket:
            request = msgpack.unpackb(raw, raw=False)
            if request["messageType"] == "AuthenticationTokenRequest":
                await asyncio.sleep(0.5)  # 留出 close() 的窗口
                await websocket.send(encode(route(request)))
                continue
            response = route(request)
            if response is not None:
                await websocket.send(encode(response))

    async with websockets.serve(handler, "127.0.0.1", 0, subprotocols=[SUBPROTOCOL]) as server:
        port = server.sockets[0].getsockname()[1]
        session = NanaLiveSession(port=port, identity=IDENTITY, retry_delay=0.05)
        connect_task = asyncio.ensure_future(session.connect())
        await asyncio.sleep(0.1)  # 建链进行中、鉴权未完成
        await session.close()
        with pytest.raises(NanaLiveError):
            await asyncio.wait_for(connect_task, timeout=5.0)
        assert session.status == "disconnected"

        # 等服务端那只慢半拍的鉴权回复到达，会话也不得复活。
        await asyncio.sleep(0.8)
        assert not session.connected
        assert session.status == "disconnected"
        await session.close()  # 幂等
        assert connection_count <= 2


async def test_on_status_exception_does_not_kill_supervisor():
    """on_status 回调抛异常只上报，不终止自动重连。"""
    errors = []
    statuses = []

    def flaky_on_status(status):
        statuses.append(status)
        if status == "reconnecting":
            raise RuntimeError("callback boom")

    async def handler(websocket):
        async for raw in websocket:
            request = msgpack.unpackb(raw, raw=False)
            if request["messageType"] == "DropConnectionRequest":
                await websocket.close(code=1011)
                return
            response = route(request)
            if response is not None:
                await websocket.send(encode(response))

    async with websockets.serve(handler, "127.0.0.1", 0, subprotocols=[SUBPROTOCOL]) as server:
        port = server.sockets[0].getsockname()[1]
        session = NanaLiveSession(
            port=port,
            identity=IDENTITY,
            on_status=flaky_on_status,
            on_error=errors.append,
            retry_delay=0.05,
            max_retry_delay=0.1,
        )
        await session.connect()
        with pytest.raises(ConnectionLostError):
            await session.request("DropConnectionRequest")

        async def reconnected():
            try:
                await session.request("AvailableModelsRequest")
                return True
            except Exception:
                return False

        assert await wait_for(reconnected), "回调异常后重连应继续工作"
        assert any("on_status callback error" in e for e in errors)
        await session.close()


async def test_aiohttp_transport_backend():
    """aiohttp 后端完成连接 + 鉴权 + 请求。"""
    pytest.importorskip("aiohttp")

    async def handler(websocket):
        async for raw in websocket:
            response = route(msgpack.unpackb(raw, raw=False))
            if response is not None:
                await websocket.send(encode(response))

    async with websockets.serve(handler, "127.0.0.1", 0, subprotocols=[SUBPROTOCOL]) as server:
        port = server.sockets[0].getsockname()[1]
        session = NanaLiveSession(port=port, identity=IDENTITY, transport="aiohttp")
        await session.connect()
        models = await session.request("AvailableModelsRequest")
        assert models["data"]["models"] == [{"modelID": "m-1"}]
        await session.close()


async def test_aiohttp_transport_reconnects_after_drop():
    """aiohttp 后端同样支持断线重连。"""
    pytest.importorskip("aiohttp")
    connections = 0

    async def handler(websocket):
        nonlocal connections
        connections += 1
        index = connections
        async for raw in websocket:
            request = msgpack.unpackb(raw, raw=False)
            response = route(request)
            if response is None:
                continue
            await websocket.send(encode(response))
            if index == 1 and request["messageType"] == "AvailableModelsRequest":
                await websocket.close(code=1011)
                return

    async with websockets.serve(handler, "127.0.0.1", 0, subprotocols=[SUBPROTOCOL]) as server:
        port = server.sockets[0].getsockname()[1]
        session = NanaLiveSession(
            port=port,
            identity=IDENTITY,
            transport="aiohttp",
            retry_delay=0.05,
            max_retry_delay=0.1,
        )
        await session.connect()
        # 先显式发一次请求触发服务端"回答后断开"；响应与断开赛跑，成败皆可。
        try:
            await session.request("AvailableModelsRequest")
        except Exception:
            pass

        async def reconnected():
            try:
                models = await session.request("AvailableModelsRequest")
                return models["data"]["models"] == [{"modelID": "m-1"}]
            except Exception:
                return False

        assert await wait_for(reconnected)
        assert connections >= 2
        await session.close()


async def test_custom_transport_factory_injection():
    """自定义异步工厂可直接注入（任意网络库的接入点）。"""
    calls = []

    async def custom_factory(**kwargs):
        calls.append(kwargs)
        websockets_connect = resolve_transport("websockets")
        return await websockets_connect(**kwargs)

    async def handler(websocket):
        async for raw in websocket:
            response = route(msgpack.unpackb(raw, raw=False))
            if response is not None:
                await websocket.send(encode(response))

    async with websockets.serve(handler, "127.0.0.1", 0, subprotocols=[SUBPROTOCOL]) as server:
        port = server.sockets[0].getsockname()[1]
        session = NanaLiveSession(port=port, identity=IDENTITY, transport=custom_factory)
        await session.connect()
        models = await session.request("AvailableModelsRequest")
        assert models["data"]["models"] == [{"modelID": "m-1"}]
        await session.close()
        assert len(calls) == 1
        assert calls[0]["subprotocol"] == SUBPROTOCOL
        assert "host" in calls[0] and "port" in calls[0]


async def test_unknown_transport_name_raises():
    with pytest.raises(NanaLiveError, match="未知传输后端"):
        await connect(port=1, transport="nope", open_timeout=0.1)


async def test_transport_closed_on_dead_connection_send():
    """对已关闭的传输 send 抛 TransportClosed（统一出站泵的错误面）。"""

    async def handler(websocket):
        await websocket.wait_closed()

    async with websockets.serve(handler, "127.0.0.1", 0, subprotocols=[SUBPROTOCOL]) as server:
        port = server.sockets[0].getsockname()[1]
        connection = await connect(host="127.0.0.1", port=port)
        await connection.close()
        with pytest.raises(TransportClosed):
            await connection.transport.send(b"x")
