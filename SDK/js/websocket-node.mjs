import crypto from "node:crypto";
import net from "node:net";

// RFC 6455 固定 GUID，用于校验服务端 Sec-WebSocket-Accept。
const WEBSOCKET_GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

// 单条消息（含分片重组结果）的默认上限，防恶意服务端把内存吃光。
export const DEFAULT_MAX_MESSAGE_BYTES = 16 * 1024 * 1024;

export function connectTextWebSocket(options) {
  return connectWebSocket({ ...options, binary: false });
}

export function connectBinaryWebSocket(options) {
  return connectWebSocket({ ...options, binary: true });
}

function connectWebSocket({
  host = "127.0.0.1",
  port,
  subprotocol,
  onMessage,
  onClose,
  onError,
  onPong,
  binary = false,
  connectTimeout = null,
  maxMessageBytes = DEFAULT_MAX_MESSAGE_BYTES,
}) {
  // host/port 会原样写进请求头，先挡住头注入与非法值。
  if (/[\r\n]/.test(String(host))) {
    throw new Error("invalid_host");
  }
  if (!Number.isInteger(port) || port < 1 || port > 65535) {
    throw new Error("invalid_port");
  }

  return new Promise((resolve, reject) => {
    const socket = net.connect({ host, port });
    const key = crypto.randomBytes(16).toString("base64");
    const expectedAccept = crypto
      .createHash("sha1")
      .update(key + WEBSOCKET_GUID)
      .digest("base64");
    let handshake = true;
    let settled = false;
    let closeHandled = false;
    let buffer = Buffer.alloc(0);
    let timer = null;
    // 帧分片重组状态（opcode 0 continuation）。
    let fragmentPayload = null;
    let fragmentText = false;

    function fail(error) {
      if (!settled) {
        settled = true;
        reject(error);
      }
      onError?.(error);
      socket.destroy();
    }

    function handleClose() {
      // 收到 close 帧和 socket 'close' 事件可能先后到达，只通知一次。
      if (closeHandled) return;
      closeHandled = true;
      onClose?.();
    }

    function deliverMessage(isText, payload) {
      if (isText && !binary) onMessage?.(payload.toString("utf8"));
      else if (!isText && binary) onMessage?.(payload);
    }

    if (connectTimeout !== null && connectTimeout !== undefined) {
      timer = setTimeout(() => fail(new Error("connect_timeout")), connectTimeout);
      socket.once("close", () => clearTimeout(timer));
    }

    socket.on("connect", () => {
      socket.write(
        [
          "GET / HTTP/1.1",
          `Host: ${host}:${port}`,
          "Upgrade: websocket",
          "Connection: Upgrade",
          `Sec-WebSocket-Key: ${key}`,
          "Sec-WebSocket-Version: 13",
          ...(subprotocol ? [`Sec-WebSocket-Protocol: ${subprotocol}`] : []),
          "\r\n",
        ].join("\r\n"),
      );
    });

    socket.on("data", (chunk) => {
      buffer = Buffer.concat([buffer, chunk]);
      if (buffer.length > maxMessageBytes + 2048) {
        fail(new Error("frame_too_large"));
        return;
      }
      if (handshake) {
        const index = buffer.indexOf("\r\n\r\n");
        if (index === -1) return;
        const header = buffer.subarray(0, index).toString("utf8");
        buffer = buffer.subarray(index + 4);
        if (!header.startsWith("HTTP/1.1 101") || !header.includes(expectedAccept)) {
          fail(new Error("websocket_upgrade_failed"));
          return;
        }
        handshake = false;
        if (!settled) {
          settled = true;
          if (timer !== null) clearTimeout(timer);
          resolve({
            send(payload) {
              const data = binary
                ? Buffer.isBuffer(payload)
                  ? payload
                  : Buffer.from(payload)
                : Buffer.from(String(payload), "utf8");
              socket.write(encodeFrame(binary ? 2 : 1, data));
            },
            ping() {
              socket.write(encodeFrame(9, Buffer.alloc(0)));
            },
            close() {
              socket.end();
            },
          });
        }
      }
      while (!handshake) {
        const frame = decodeFrame(buffer);
        if (!frame) break;
        buffer = frame.rest;
        if (frame.opcode === 1 || frame.opcode === 2) {
          if (frame.fin) {
            deliverMessage(frame.opcode === 1, frame.payload);
          } else {
            // 分片消息的第一片。
            fragmentPayload = frame.payload;
            fragmentText = frame.opcode === 1;
          }
        } else if (frame.opcode === 0) {
          if (fragmentPayload === null) {
            fail(new Error("protocol_error"));
            return;
          }
          fragmentPayload = Buffer.concat([fragmentPayload, frame.payload]);
          if (fragmentPayload.length > maxMessageBytes) {
            fail(new Error("message_too_large"));
            return;
          }
          if (frame.fin) {
            const payload = fragmentPayload;
            fragmentPayload = null;
            deliverMessage(fragmentText, payload);
          }
        } else if (frame.opcode === 9) {
          socket.write(encodeFrame(10, frame.payload));
        } else if (frame.opcode === 10) {
          onPong?.(frame.payload);
        } else if (frame.opcode === 8) {
          handleClose();
          socket.end();
        }
      }
    });

    socket.on("error", (error) => fail(error));
    socket.on("close", () => {
      handleClose();
    });
  });
}

function encodeFrame(opcode, payload) {
  const mask = crypto.randomBytes(4);
  const masked = Buffer.alloc(payload.length);
  for (let index = 0; index < payload.length; index += 1) {
    masked[index] = payload[index] ^ mask[index % 4];
  }
  const fin = 0x80;
  let header;
  if (payload.length < 126) {
    header = Buffer.from([fin | opcode, 0x80 | payload.length]);
  } else if (payload.length < 65536) {
    header = Buffer.alloc(4);
    header[0] = fin | opcode;
    header[1] = 0x80 | 126;
    header.writeUInt16BE(payload.length, 2);
  } else {
    header = Buffer.alloc(10);
    header[0] = fin | opcode;
    header[1] = 0x80 | 127;
    header.writeBigUInt64BE(BigInt(payload.length), 2);
  }
  return Buffer.concat([header, mask, masked]);
}

function decodeFrame(buffer) {
  if (buffer.length < 2) return null;
  const fin = (buffer[0] & 0x80) !== 0;
  const opcode = buffer[0] & 0x0f;
  const masked = (buffer[1] & 0x80) !== 0;
  let length = buffer[1] & 0x7f;
  let offset = 2;
  if (length === 126) {
    if (buffer.length < 4) return null;
    length = buffer.readUInt16BE(2);
    offset = 4;
  } else if (length === 127) {
    if (buffer.length < 10) return null;
    length = Number(buffer.readBigUInt64BE(2));
    offset = 10;
  }
  if (masked) offset += 4;
  if (buffer.length < offset + length) return null;
  let payload = buffer.subarray(offset, offset + length);
  if (masked) {
    const mask = buffer.subarray(offset - 4, offset);
    const next = Buffer.alloc(length);
    for (let index = 0; index < length; index += 1) {
      next[index] = payload[index] ^ mask[index % 4];
    }
    payload = next;
  }
  return { fin, opcode, payload, rest: buffer.subarray(offset + length) };
}
