import { createNanaLiveClient, DEFAULT_PORT, SUBPROTOCOL } from "./api.mjs";
import { connectBinaryWebSocket } from "./websocket-node.mjs";

const CONNECTING = "connecting";
const CONNECTED = "connected";
const RECONNECTING = "reconnecting";
const DISCONNECTED = "disconnected";

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
  // 每次调用 connect() 递增：旧的后台任务在下一个检查点自行退出。
  let episode = 0;
  let supervisorDone = null;
  let status = DISCONNECTED;
  let attempt = 0;

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
      if (Date.now() - lastActivity >= heartbeatInterval) {
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
    const base = Math.min(retryDelay * 2 ** Math.max(0, attempt - 1), maxRetryDelay);
    return Math.max(0, base + base * 0.2 * (Math.random() * 2 - 1));
  }

  function isCurrent(myEpisode) {
    return !closed && episode === myEpisode;
  }

  // 建立一次连接并完成鉴权；失败时清理半开连接后向上抛。
  async function establish(myEpisode) {
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
          // 配对过的响应带 requestID；只有服务器主动推送才透传。
          if (decoded && decoded.requestID === undefined) {
            onUnhandled?.(decoded);
          }
        },
        onPong: noteActivity,
        onClose: () => handleDisconnect(socket),
        onError: (error) => onError?.(error),
      });
      if (!isCurrent(myEpisode)) throw new Error("superseded");
      currentSocket = socket;
      const disconnected = new Promise((resolve) => {
        resolveDisconnected = resolve;
      });
      startHeartbeat(socket);
      await client.authenticate();
      if (!isCurrent(myEpisode)) throw new Error("superseded");
      attempt = 0;
      setStatus(CONNECTED);
      // 注意：不要直接 return 这个 Promise——async 函数会采纳它，
      // 导致 await establish() 一直等到断线才返回。
      return { disconnected };
    } catch (error) {
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
      throw error;
    }
  }

  // 断开后的后台重连循环；episode 变化或 close() 时退出。
  async function supervise(myEpisode, disconnected) {
    while (isCurrent(myEpisode)) {
      await disconnected;
      if (!isCurrent(myEpisode)) return;
      if (!reconnect) {
        setStatus(DISCONNECTED);
        return;
      }
      while (true) {
        attempt += 1;
        if (maxRetries !== null && attempt > maxRetries) {
          setStatus(DISCONNECTED);
          return;
        }
        setStatus(RECONNECTING);
        await sleep(backoffDelay());
        if (!isCurrent(myEpisode)) return;
        try {
          disconnected = (await establish(myEpisode)).disconnected;
          break;
        } catch (error) {
          if (!isCurrent(myEpisode)) return;
        }
      }
    }
  }

  // 内联重试直到首个连接完成鉴权；之后的断线交给后台任务。
  async function connect() {
    closed = false;
    episode += 1;
    const myEpisode = episode;
    const previous = supervisorDone;
    supervisorDone = null;
    if (previous) await previous;
    stopHeartbeat();
    if (currentSocket) {
      try {
        currentSocket.close();
      } catch {
        // 忽略关闭失败。
      }
      currentSocket = null;
    }
    const signal = resolveDisconnected;
    resolveDisconnected = null;
    signal?.();

    while (true) {
      setStatus(attempt === 0 ? CONNECTING : RECONNECTING);
      try {
        const { disconnected } = await establish(myEpisode);
        supervisorDone = supervise(myEpisode, disconnected).catch(() => {});
        return;
      } catch (error) {
        attempt += 1;
        if (!isCurrent(myEpisode) || !reconnect || (maxRetries !== null && attempt > maxRetries)) {
          setStatus(DISCONNECTED);
          throw error;
        }
        await sleep(backoffDelay());
      }
    }
  }

  async function close() {
    if (closed) return;
    closed = true;
    episode += 1;
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
    if (supervisorDone) await supervisorDone;
    setStatus(DISCONNECTED);
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
      return status === CONNECTED && currentSocket !== null;
    },
  };
}
