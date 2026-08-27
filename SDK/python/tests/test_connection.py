"""连接帮手集成测试：本地 mock WebSocket 服务端 + 真实连接流程。"""

import msgpack
import pytest
import websockets

from nanalive_sdk import (
    API_NAME,
    API_VERSION,
    SUBPROTOCOL,
    connect,
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


async def test_connect_authenticate_and_list_models():
    issued = []
    seen_subprotocols = []

    async def handler(websocket):
        seen_subprotocols.append(websocket.subprotocol)
        async for raw in websocket:
            response = route(msgpack.unpackb(raw, raw=False))
            if response is not None:
                await websocket.send(encode(response))

    async with websockets.serve(handler, "127.0.0.1", 0, subprotocols=[SUBPROTOCOL]) as server:
        port = server.sockets[0].getsockname()[1]
        connection = await connect(
            host="127.0.0.1",
            port=port,
            identity=IDENTITY,
            on_token=issued.append,
        )
        try:
            await connection.client.authenticate()
            models = await connection.client.list_models()
            assert models["data"]["models"] == [{"modelID": "m-1"}]
        finally:
            await connection.close()

    assert issued == ["issued-token"]
    assert seen_subprotocols == [SUBPROTOCOL]
