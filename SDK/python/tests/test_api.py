"""协议客户端单元测试：envelope、配对、鉴权与助手函数。"""

import asyncio

import msgpack
import pytest

from nanalive_sdk import (
    API_NAME,
    API_VERSION,
    AuthenticationTokenMissingError,
    NanaLiveClient,
    NanaLiveError,
    executable_hotkeys,
    parameter_value_after_ticks,
    write_parameter_command,
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


class Mock:
    def __init__(self, identity=None, token=None, on_token=None):
        self.sent = []
        self.client = NanaLiveClient(
            send=self.sent.append,
            identity=identity,
            token=token,
            on_token=on_token,
        )


async def test_envelope_has_fixed_fields_and_increasing_request_ids():
    mock = Mock(identity={"pluginID": "x"})
    task = asyncio.create_task(mock.client.request("AvailableModelsRequest"))
    while not mock.sent:
        await asyncio.sleep(0)
    first = msgpack.unpackb(mock.sent[0], raw=False)

    assert first["apiName"] == API_NAME
    assert first["apiVersion"] == API_VERSION
    assert first["messageType"] == "AvailableModelsRequest"
    assert first["data"] == {}
    assert first["requestID"] == "nanalive-1"
    task.cancel()

    task = asyncio.create_task(mock.client.request("MotionListRequest"))
    while len(mock.sent) < 2:
        await asyncio.sleep(0)
    second = msgpack.unpackb(mock.sent[1], raw=False)
    assert second["requestID"] == "nanalive-2"
    task.cancel()


async def test_response_is_paired_and_unhandled_push_is_passed_through():
    mock = Mock()
    task = asyncio.create_task(mock.client.request("HotkeyListRequest"))
    while not mock.sent:
        await asyncio.sleep(0)
    sent = msgpack.unpackb(mock.sent[0], raw=False)
    request_id = sent["requestID"]

    push = {"apiName": API_NAME, "requestID": "nanalive-other", "messageType": "SomePush", "data": {}}
    assert mock.client.receive(push) == push

    response = envelope(request_id, "HotkeyListResponse", {"hotkeys": []})
    assert mock.client.receive(response) is None
    result = await task
    assert result["data"] == {"hotkeys": []}


async def test_api_error_response_rejects_with_code():
    mock = Mock()
    task = asyncio.create_task(mock.client.request("MotionTriggerRequest", {"motionID": "m1"}))
    while not mock.sent:
        await asyncio.sleep(0)
    sent = msgpack.unpackb(mock.sent[0], raw=False)

    error = envelope(sent["requestID"], "APIError", {"message": "motion not found", "errorCode": "motion_not_found"})
    mock.client.receive(error)

    with pytest.raises(NanaLiveError) as exc_info:
        await task
    assert str(exc_info.value) == "motion not found"
    assert exc_info.value.code == "motion_not_found"


async def test_authentication_token_request_sends_identity_or_nil():
    mock = Mock()
    task = asyncio.create_task(mock.client.authenticate())
    while not mock.sent:
        await asyncio.sleep(0)
    sent = msgpack.unpackb(mock.sent[0], raw=False)
    assert sent["messageType"] == "AuthenticationTokenRequest"
    assert sent["data"] is None
    task.cancel()

    identity_mock = Mock(identity={"pluginID": "dev.example.plugin"})
    task = asyncio.create_task(identity_mock.client.authenticate())
    while not identity_mock.sent:
        await asyncio.sleep(0)
    sent = msgpack.unpackb(identity_mock.sent[0], raw=False)
    assert sent["data"] == {"pluginID": "dev.example.plugin"}
    task.cancel()


async def test_authenticate_with_valid_token_only_verifies_once():
    mock = Mock(token="saved-token")
    task = asyncio.create_task(mock.client.authenticate())
    while not mock.sent:
        await asyncio.sleep(0)
    sent = msgpack.unpackb(mock.sent[0], raw=False)

    assert sent["messageType"] == "AuthenticationRequest"
    assert sent["data"] == {"authenticationToken": "saved-token"}

    mock.client.receive(envelope(sent["requestID"], "AuthenticationResponse", {}))
    await task
    assert len(mock.sent) == 1


async def test_authenticate_falls_back_when_saved_token_is_rejected():
    issued = []
    identity = {
        "pluginID": "dev.example.plugin",
        "pluginName": "Example",
        "pluginDeveloper": "Example",
        "pluginVersion": "0.1.0",
        "scopes": ["model.read"],
    }
    mock = Mock(identity=identity, token="stale-token", on_token=issued.append)
    task = asyncio.create_task(mock.client.authenticate())

    # 第一步：旧 token 验证被拒。
    while len(mock.sent) < 1:
        await asyncio.sleep(0)
    sent = msgpack.unpackb(mock.sent[0], raw=False)
    assert sent["messageType"] == "AuthenticationRequest"
    mock.client.receive(
        envelope(sent["requestID"], "APIError", {"message": "invalid token"})
    )

    # 第二步：降级申请新 token。
    while len(mock.sent) < 2:
        await asyncio.sleep(0)
    sent = msgpack.unpackb(mock.sent[1], raw=False)
    assert sent["messageType"] == "AuthenticationTokenRequest"
    assert sent["data"] == identity
    mock.client.receive(
        envelope(sent["requestID"], "AuthenticationTokenResponse", {"authenticationToken": "fresh-token"})
    )

    # 第三步：用新 token 验证。
    while len(mock.sent) < 3:
        await asyncio.sleep(0)
    sent = msgpack.unpackb(mock.sent[2], raw=False)
    assert sent["messageType"] == "AuthenticationRequest"
    assert sent["data"] == {"authenticationToken": "fresh-token"}
    mock.client.receive(envelope(sent["requestID"], "AuthenticationResponse", {}))

    await task
    assert issued == ["fresh-token"]


async def test_authenticate_fails_when_no_token_is_issued():
    mock = Mock()
    task = asyncio.create_task(mock.client.authenticate())
    while not mock.sent:
        await asyncio.sleep(0)
    sent = msgpack.unpackb(mock.sent[0], raw=False)
    mock.client.receive(envelope(sent["requestID"], "AuthenticationTokenResponse", {}))

    with pytest.raises(AuthenticationTokenMissingError):
        await task


def test_executable_hotkeys_filters_on_executable_flag():
    hotkeys = [
        {"hotkeyID": "h1", "executable": True},
        {"hotkeyID": "h2", "executable": False},
        {"hotkeyID": "h3"},
    ]
    executable = executable_hotkeys(hotkeys)
    assert [hotkey["hotkeyID"] for hotkey in executable] == ["h1"]


def test_parameter_value_after_ticks_clamps_to_range():
    # 每格 0.5（量程 0..20 除以 40）。
    parameter = {"value": 10.0, "min": 0.0, "max": 20.0}
    assert parameter_value_after_ticks(parameter, 0.0) == 10.0
    assert parameter_value_after_ticks(parameter, 4.0) == 12.0
    assert parameter_value_after_ticks(parameter, -4.0) == 8.0
    assert parameter_value_after_ticks(parameter, 400.0) == 20.0
    assert parameter_value_after_ticks(parameter, -400.0) == 0.0
    assert parameter_value_after_ticks(parameter, float("nan")) == 10.0
    # 无参数回退 0，span 为 0 时步长为 1，但仍钳制在 min==max 上。
    assert parameter_value_after_ticks(None, 3.0) == 0.0
    flat = {"value": 7.0, "min": 5.0, "max": 5.0}
    assert parameter_value_after_ticks(flat, 2.0) == 5.0


def test_write_parameter_command_validates_input():
    command = write_parameter_command("ParamA", 3.5)
    assert command == {
        "messageType": "ParameterWriteRequest",
        "data": {"parameters": {"ParamA": 3.5}},
    }

    assert write_parameter_command(None, 1.0) is None
    assert write_parameter_command("", 1.0) is None
    assert write_parameter_command("ParamA", float("nan")) is None
