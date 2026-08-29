"""会话层集成测试：自动重连、请求失败、请求超时与关闭语义。"""

import asyncio

import msgpack
import pytest
import websockets

from nanalive_sdk import (
    API_NAME,
    API_VERSION,
    SUBPROTOCOL,
    ConnectionLostError,
    NanaLiveSession,
    NotConnectedError,
    RequestTimeoutError,
)


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


def make_server(*, drop_after_first_models: bool, answer_models: bool = True):
    """本地 mock 服务端；``drop_after_first_models`` 模拟第一条连接上的服务器崩溃。"""
    connection_count = 0

    async def handler(websocket):
        nonlocal connection_count
        connection_count += 1
        index = connection_count
        async for raw in websocket:
            request = msgpack.unpackb(raw, raw=False)
            response = route(request)
            if response is None:
                continue
            if not answer_models and response["messageType"] == "AvailableModelsResponse":
                continue  # 不回答，用于请求超时测试
            await websocket.send(encode(response))
            if (
                index == 1
                and drop_after_first_models
                and response["messageType"] == "AvailableModelsResponse"
            ):
                # 模拟服务器崩溃：直接断开，让会话层重连。
                await websocket.close(code=1011)
                return

    return handler


async def wait_for(predicate, timeout=5.0, interval=0.05):
    deadline = asyncio.get_running_loop().time() + timeout
    while asyncio.get_running_loop().time() < deadline:
        if await predicate() if callable(predicate) else predicate:
            return True
        await asyncio.sleep(interval)
    return False


async def test_reconnect_after_server_drop():
    issued = []
    statuses = []

    async with websockets.serve(
        make_server(drop_after_first_models=True), "127.0.0.1", 0, subprotocols=[SUBPROTOCOL]
    ) as server:
        port = server.sockets[0].getsockname()[1]
        session = NanaLiveSession(
            port=port,
            identity=IDENTITY,
            on_token=issued.append,
            on_status=statuses.append,
            retry_delay=0.05,
            max_retry_delay=0.1,
        )
        await session.connect()
        first = await session.request("AvailableModelsRequest")
        assert first["data"]["models"] == [{"modelID": "m-1"}]

        # 第一条连接在回答后就被服务端断开；重连后再次查询应成功。
        async def reconnected():
            try:
                models = await session.request("AvailableModelsRequest")
                return models["data"]["models"] == [{"modelID": "m-1"}]
            except Exception:
                return False

        assert await wait_for(reconnected), "重连后未能再次完成请求"
        await session.close()

    assert issued == ["issued-token"]
    assert statuses.count("connected") >= 2
    assert "reconnecting" in statuses
    assert statuses[-1] == "disconnected"


async def test_request_timeout_and_not_connected():
    async with websockets.serve(
        make_server(drop_after_first_models=False, answer_models=False),
        "127.0.0.1",
        0,
        subprotocols=[SUBPROTOCOL],
    ) as server:
        port = server.sockets[0].getsockname()[1]
        session = NanaLiveSession(port=port, identity=IDENTITY, request_timeout=0.2)

        with pytest.raises(NotConnectedError):
            await session.request("AvailableModelsRequest")

        await session.connect()
        with pytest.raises(RequestTimeoutError):
            await session.request("AvailableModelsRequest")
        await session.close()

        with pytest.raises(NotConnectedError):
            await session.request("AvailableModelsRequest")


async def test_pending_requests_fail_on_drop():
    dropped = asyncio.Event()

    async def handler(websocket):
        async for raw in websocket:
            request = msgpack.unpackb(raw, raw=False)
            # 只回答鉴权，收到模型目录请求时直接断开。
            if request["messageType"] == "AvailableModelsRequest":
                dropped.set()
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
        pending = asyncio.ensure_future(session.request("AvailableModelsRequest"))
        await dropped.wait()
        with pytest.raises(ConnectionLostError):
            await pending
        await session.close()
