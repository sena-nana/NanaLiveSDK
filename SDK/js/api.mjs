import { decode, encode } from "./msgpack.mjs";

export const API_NAME = "NanaLiveControlAPI";
export const API_VERSION = "2.0";
export const SUBPROTOCOL = "nanalive-control-v2";
export const DEFAULT_PORT = 8312;

/** 服务端 `APIError` 的 `data.errorCode`；`retryable` 为真的那些稍后重试可能成功。 */
export const ErrorCode = Object.freeze({
  UNSUPPORTED_API_VERSION: "UNSUPPORTED_API_VERSION",
  INVALID_REQUEST: "INVALID_REQUEST",
  MESSAGE_TOO_LARGE: "MESSAGE_TOO_LARGE",
  AUTHENTICATION_REQUIRED: "AUTHENTICATION_REQUIRED",
  AUTHENTICATION_TOKEN_INVALID: "AUTHENTICATION_TOKEN_INVALID",
  AUTHENTICATION_REVOKED: "AUTHENTICATION_REVOKED",
  USER_DENIED: "USER_DENIED",
  USER_CONFIRMATION_TIMEOUT: "USER_CONFIRMATION_TIMEOUT",
  PERMISSION_DENIED: "PERMISSION_DENIED",
  SCOPE_UPGRADE_REQUIRED: "SCOPE_UPGRADE_REQUIRED",
  DISPLAY_UNAVAILABLE: "DISPLAY_UNAVAILABLE",
  MODEL_NOT_LOADED: "MODEL_NOT_LOADED",
  MODEL_NOT_FOUND: "MODEL_NOT_FOUND",
  MODEL_SWITCH_IN_PROGRESS: "MODEL_SWITCH_IN_PROGRESS",
  EXPRESSION_NOT_FOUND: "EXPRESSION_NOT_FOUND",
  MOTION_NOT_FOUND: "MOTION_NOT_FOUND",
  HOTKEY_NOT_FOUND: "HOTKEY_NOT_FOUND",
  HOTKEY_NOT_EXECUTABLE: "HOTKEY_NOT_EXECUTABLE",
  ITEM_NOT_FOUND: "ITEM_NOT_FOUND",
  ART_MESH_NOT_FOUND: "ART_MESH_NOT_FOUND",
  COOLDOWN_ACTIVE: "COOLDOWN_ACTIVE",
  PROMPT_PENDING: "PROMPT_PENDING",
  CONTROLLED_BY_OTHER_PLUGIN: "CONTROLLED_BY_OTHER_PLUGIN",
  UNSUPPORTED: "UNSUPPORTED",
  CONTROL_BUSY: "CONTROL_BUSY",
  RATE_LIMITED: "RATE_LIMITED",
  INTERNAL_ERROR: "INTERNAL_ERROR",
});

/** 稍后重试可能成功的错误码，与 `data.retryable` 一致。 */
export const RETRYABLE_ERROR_CODES = Object.freeze([
  ErrorCode.USER_CONFIRMATION_TIMEOUT,
  ErrorCode.DISPLAY_UNAVAILABLE,
  ErrorCode.MODEL_SWITCH_IN_PROGRESS,
  ErrorCode.COOLDOWN_ACTIVE,
  ErrorCode.PROMPT_PENDING,
  ErrorCode.CONTROLLED_BY_OTHER_PLUGIN,
  ErrorCode.CONTROL_BUSY,
  ErrorCode.RATE_LIMITED,
  ErrorCode.INTERNAL_ERROR,
]);

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

  function requestWithId(messageType, data = {}) {
    sequence += 1;
    const requestID = `nanalive-${sequence}`;
    let resolveWaiter;
    let rejectWaiter;
    const promise = new Promise((resolve, reject) => {
      resolveWaiter = resolve;
      rejectWaiter = reject;
    });
    waiters.set(requestID, { resolve: resolveWaiter, reject: rejectWaiter });
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
      rejectWaiter(error);
    }
    return { requestID, promise };
  }

  function request(messageType, data = {}) {
    return requestWithId(messageType, data).promise;
  }

  // 超时放弃等待后注销 waiter：Map 不积累，迟到响应被静默吸收。
  function cancelRequest(requestID) {
    waiters.delete(requestID);
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
    // 服务端数据形状不可信：非对象/缺 requestID 一律按推送透传。
    const requestID = typeof response?.requestID === "string" ? response.requestID : undefined;
    const waiter = requestID !== undefined ? waiters.get(requestID) : undefined;
    if (!waiter) return response;
    waiters.delete(requestID);
    if (response.messageType === "APIError") {
      const data =
        response.data !== null && typeof response.data === "object" ? response.data : {};
      const error = new Error(data.message ?? "api_error");
      error.code = data.errorCode;
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
      } catch (error) {
        // 只有服务端明确拒绝（APIError 携带 code）才轮换 token；
        // 网络闪断、超时等传输层故障原样上抛，避免无谓重发签发请求。
        if (!error?.code) throw error;
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
    requestWithId,
    cancelRequest,
    authenticate,
    failPending,
    listModels: () => request("AvailableModelsRequest"),
    listMotions: () => request("MotionListRequest"),
    listExpressions: () => request("ExpressionListRequest"),
    listHotkeys: () => request("HotkeyListRequest"),
    listParameters: () => request("ParameterListRequest"),
  };
}
