import { decode, encode } from "./msgpack.mjs";

export const API_NAME = "NanaLiveControlAPI";
export const API_VERSION = "2.0";
export const SUBPROTOCOL = "nanalive-control-v2";
export const DEFAULT_PORT = 8312;

export function executableHotkeys(hotkeys = []) {
  return hotkeys.filter((hotkey) => hotkey?.executable === true);
}

export function parameterValueAfterTicks(parameter, ticks) {
  if (!parameter) return 0;
  const value = Number(parameter.value);
  const min = Number(parameter.min);
  const max = Number(parameter.max);
  if (!Number.isFinite(ticks) || ticks === 0) {
    return Number.isFinite(value) ? value : 0;
  }
  const span = max - min;
  const step = span === 0 || !Number.isFinite(span) ? 1 : span / 40;
  const next = value + ticks * step;
  if (!Number.isFinite(next)) return value;
  return Math.min(max, Math.max(min, next));
}

export function writeParameterCommand(parameterID, value) {
  if (!parameterID || !Number.isFinite(value)) return null;
  return {
    messageType: "ParameterWriteRequest",
    data: { parameters: { [parameterID]: value } },
  };
}

export function createNanaLiveClient({ send, identity = null, token = null, onToken } = {}) {
  if (typeof send !== "function") {
    throw new Error("send is required");
  }
  const waiters = new Map();
  let sequence = 0;
  let authenticationToken = token;

  function request(messageType, data = {}) {
    sequence += 1;
    const requestID = `nanalive-${sequence}`;
    const result = new Promise((resolve, reject) => {
      waiters.set(requestID, { resolve, reject });
    });
    try {
      send(
        encode({
          apiName: API_NAME,
          apiVersion: API_VERSION,
          requestID,
          messageType,
          data,
        }),
      );
    } catch (error) {
      waiters.delete(requestID);
      throw error;
    }
    return result;
  }

  function failPending(error) {
    const pending = [...waiters.values()];
    waiters.clear();
    for (const waiter of pending) {
      waiter.reject(error);
    }
    return pending.length;
  }

  function receive(raw) {
    const response =
      raw instanceof Uint8Array || ArrayBuffer.isView(raw) || raw instanceof ArrayBuffer
        ? decode(raw)
        : raw;
    const waiter = waiters.get(response.requestID);
    if (!waiter) return response;
    waiters.delete(response.requestID);
    if (response.messageType === "APIError") {
      const error = new Error(response.data?.message ?? "api_error");
      error.code = response.data?.errorCode;
      waiter.reject(error);
    } else {
      waiter.resolve(response);
    }
    return response;
  }

  async function authenticate() {
    if (authenticationToken) {
      try {
        return await request("AuthenticationRequest", { authenticationToken });
      } catch {
        authenticationToken = null;
      }
    }
    const issued = await request("AuthenticationTokenRequest", identity);
    authenticationToken = issued.data?.authenticationToken ?? null;
    if (!authenticationToken) throw new Error("authentication_token_missing");
    onToken?.(authenticationToken);
    return request("AuthenticationRequest", { authenticationToken });
  }

  return {
    receive,
    request,
    authenticate,
    failPending,
    listModels: () => request("AvailableModelsRequest"),
    listMotions: () => request("MotionListRequest"),
    listExpressions: () => request("ExpressionListRequest"),
    listHotkeys: () => request("HotkeyListRequest"),
    listParameters: () => request("ParameterListRequest"),
  };
}
