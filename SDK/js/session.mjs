import { createNanaLiveClient, DEFAULT_PORT, SUBPROTOCOL } from "./api.mjs";
import { connectBinaryWebSocket } from "./websocket-node.mjs";

const SESSION_CONNECTING = "connecting";
const SESSION_CONNECTED = "connected";
const SESSION_RECONNECTING = "reconnecting";
const SESSION_DISCONNECTED = "disconnected";

/**
 * 带自动重连、心跳与请求超时的会话层。
 *
 * 完整连接流程：建立 WebSocket → 鉴权（优先复用已有 token）→ 心跳保活；
 * 断线后挂起中的请求立即失败，并按指数退避（带抖动）自动重连与重新鉴权。
 * 通过 `onStatus` 回调观察 `connecting` / `connected` / `reconnecting` /
 * `disconnected` 状态变化。
 *
 * 心跳：每 `heartbeatInterval` 检查一次最近收帧时间，空闲则发 WebSocket
 * ping；`heartbeatTimeout` 内仍无任何入站帧（pong/数据）即视为死链并重连。
 */
export function createNanaLiveSession(options = {}) {
  const {
    host = "127.0.0.1",
    port = DEFAULT_PORT,
    subprotocol = SUBPROTOCOL,
    identity = null,
    token = null,
    onToken,
    onUnhandled,
    onError,
    onStatus,
    reconnect = true,
    maxRetries = null,
    retryDelay = 500,
    maxRetryDelay = 8000,
    heartbeatInterval = 10000,
    heartbeatTimeout = 5000,
    connectTimeout = 5000,
    requestTimeout = 30000,
  } = options;

  if (heartbeatInterval <= 0) throw new Error("heartbeat_interval_must_be_positive");
  if (retryDelay <= 0 || maxRetryDelay < retryDelay) {
    throw new Error("invalid_retry_delay");
  }

  let currentSocket = null;
  let lastActivity = 0;
  let heartbeatTimer = null;
  let resolveDisconnected = null;
  let closed = false;
  let supervisorRunning = false;
  let supervisorDone = null;
  let status = SESSION_DISCONNECTED;
  let attempt = 0;
  const connectWaiters = [];

  const client = createNanaLiveClient({
    send: (payload) => {
      if (!currentSocket) throw new Error("not_connected");
      currentSocket.send(payload);
    },
    identity,
    token,
    onToken,
  });

  function setStatus(next) {
    if (status === next) return;
    status = next;
    onStatus?.(next);
    if (next === SESSION_CONNECTED) {
      for (const waiter of connectWaiters.splice(0)) waiter.resolve();
    }
  }

  function failConnectWaiters(error) {
    for (const waiter of connectWaiters.splice(0)) waiter.reject(error);
  }

  function noteActivity() {
    lastActivity = Date.now();
  }

  function stopHeartbeat() {
    if (heartbeatTimer !== null) {
      clearInterval(heartbeatTimer);
      heartbeatTimer = null;
    }
  }

  function startHeartbeat(socket) {
    stopHeartbeat();
    noteActivity();
    heartbeatTimer = setInterval(() => {
      const idle = Date.now() - lastActivity;
      if (idle >= heartbeatInterval) {
        try {
          socket.ping();
        } catch {
          // 发送失败说明连接已坏，等 close 事件触发重连。
        }
      }
      if (Date.now() - lastActivity >= heartbeatInterval + heartbeatTimeout) {
        socket.close();
      }
    }, heartbeatInterval);
    if (typeof heartbeatTimer.unref === "function") heartbeatTimer.unref();
  }

  function handleDisconnect(socket) {
    if (currentSocket !== socket) return;
    currentSocket = null;
    stopHeartbeat();
    client.failPending(new Error("connection_lost"));
    const signal = resolveDisconnected;
    resolveDisconnected = null;
    signal?.();
  }

  function sleep(ms) {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }

  function backoffDelay() {
    const base = Math.min(retryDelay * 2 ** attempt, maxRetryDelay);
    const jitter = base * 0.2 * (Math.random() * 2 - 1);
    return Math.max(0, base + jitter);
  }

  async function run() {
    let failure = null;
    while (!closed) {
      setStatus(attempt === 0 ? SESSION_CONNECTING : SESSION_RECONNECTING);
      let socket = null;
      try {
        socket = await connectBinaryWebSocket({
          host,
          port,
          subprotocol,
          connectTimeout,
          onMessage: (payload) => {
            noteActivity();
            let decoded;
            try {
              decoded = client.receive(payload);
            } catch (error) {
              onError?.(error);
              return;
            }
            // 配对过的响应会带 requestID；只有服务器主动推送才透传。
            if (decoded && decoded.requestID === undefined) {
              onUnhandled?.(decoded);
            }
          },
          onPong: noteActivity,
          onClose: () => handleDisconnect(socket),
          onError: (error) => onError?.(error),
        });
        currentSocket = socket;
        const disconnected = new Promise((resolve) => {
          resolveDisconnected = resolve;
        });
        startHeartbeat(socket);
        await client.authenticate();
        attempt = 0;
        failure = null;
        setStatus(SESSION_CONNECTED);
        await disconnected;
      } catch (error) {
        failure = error;
        stopHeartbeat();
        currentSocket = null;
        resolveDisconnected = null;
        if (socket) {
          try {
            socket.close();
          } catch {
            // 忽略关闭失败。
          }
        }
      }

      if (closed) break;
      if (!reconnect) {
        setStatus(SESSION_DISCONNECTED);
        failConnectWaiters(failure ?? new Error("connection_lost"));
        break;
      }
      attempt += 1;
      if (maxRetries !== null && attempt > maxRetries) {
        setStatus(SESSION_DISCONNECTED);
        failConnectWaiters(failure ?? new Error("connection_lost"));
        break;
      }
      await sleep(backoffDelay());
    }
  }

  function connect() {
    if (!supervisorRunning) {
      supervisorRunning = true;
      supervisorDone = run()
        .catch(() => {})
        .finally(() => {
          supervisorRunning = false;
          supervisorDone = null;
        });
    }
    return status === SESSION_CONNECTED
      ? Promise.resolve()
      : new Promise((resolve, reject) => {
          connectWaiters.push({ resolve, reject });
        });
  }

  async function close() {
    if (closed) return;
    closed = true;
    const signal = resolveDisconnected;
    resolveDisconnected = null;
    signal?.();
    stopHeartbeat();
    if (currentSocket) {
      try {
        currentSocket.close();
      } catch {
        // 忽略关闭失败。
      }
      currentSocket = null;
    }
    client.failPending(new Error("connection_lost"));
    failConnectWaiters(new Error("closed"));
    if (supervisorDone) await supervisorDone;
    setStatus(SESSION_DISCONNECTED);
  }

  async function request(messageType, data = {}) {
    if (!currentSocket) throw new Error("not_connected");
    const pending = client.request(messageType, data);
    if (!requestTimeout) return pending;
    let timer;
    const timeout = new Promise((_, reject) => {
      timer = setTimeout(() => reject(new Error("request_timeout")), requestTimeout);
    });
    if (typeof timer.unref === "function") timer.unref();
    try {
      return await Promise.race([pending, timeout]);
    } finally {
      clearTimeout(timer);
      pending.catch(() => {});
    }
  }

  return {
    client,
    connect,
    close,
    request,
    get status() {
      return status;
    },
    get isConnected() {
      return status === SESSION_CONNECTED && currentSocket !== null;
    },
  };
}
