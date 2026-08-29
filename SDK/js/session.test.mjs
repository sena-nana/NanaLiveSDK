import assert from "node:assert/strict";
import crypto from "node:crypto";
import net from "node:net";
import test from "node:test";

import { encode, decode } from "./msgpack.mjs";
import { createNanaLiveSession } from "./session.mjs";
import { SUBPROTOCOL } from "./index.mjs";

const API_NAME = "NanaLiveControlAPI";
const API_VERSION = "2.0";
const WEBSOCKET_GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

const IDENTITY = {
  pluginID: "dev.example.plugin",
  pluginName: "Example",
  pluginDeveloper: "Example",
  pluginVersion: "0.1.0",
  scopes: ["model.read"],
};

function envelope(requestID, messageType, data) {
  return { apiName: API_NAME, apiVersion: API_VERSION, requestID, messageType, data };
}

function route(request) {
  const { requestID, messageType } = request;
  if (messageType === "AuthenticationTokenRequest") {
    return envelope(requestID, "AuthenticationTokenResponse", { authenticationToken: "issued-token" });
  }
  if (messageType === "AuthenticationRequest") {
    return envelope(requestID, "AuthenticationResponse", {});
  }
  if (messageType === "AvailableModelsRequest") {
    return envelope(requestID, "AvailableModelsResponse", { models: [{ modelID: "m-1" }] });
  }
  return null;
}

/** 服务端对 AvailableModelsRequest 的处理方式。 */
const MODELS_BEHAVIOR = {
  /** 正常回答。 */
  answer: "answer",
  /** 先回答再断开第一条连接，模拟服务器崩溃（触发自动重连）。 */
  answerThenDropFirst: "answerThenDropFirst",
  /** 不回答直接断开，模拟挂起中的请求在断线时失败。 */
  dropWithoutAnswer: "dropWithoutAnswer",
  /** 不回答也不断开，用于请求超时测试。 */
  silent: "silent",
};

/** 服务端帧：不加掩码。 */
function encodeServerFrame(opcode, payload) {
  const length = payload.length;
  let header;
  if (length < 126) {
    header = Buffer.from([0x80 | opcode, length]);
  } else if (length < 65536) {
    header = Buffer.alloc(4);
    header[0] = 0x80 | opcode;
    header[1] = 126;
    header.writeUInt16BE(length, 2);
  } else {
    header = Buffer.alloc(10);
    header[0] = 0x80 | opcode;
    header[1] = 127;
    header.writeBigUInt64BE(BigInt(length), 2);
  }
  return Buffer.concat([header, payload]);
}

/** 客户端帧：带掩码，需要去掩码。 */
function decodeClientFrame(buffer) {
  if (buffer.length < 2) return null;
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
    const unmasked = Buffer.alloc(length);
    for (let index = 0; index < length; index += 1) {
      unmasked[index] = payload[index] ^ mask[index % 4];
    }
    payload = unmasked;
  }
  return { opcode, payload, rest: buffer.subarray(offset + length) };
}

/**
 * 本地 mock 服务端：循环接受多条连接（会话层重连会用），行为与
 * Python/Rust/C# 测试里的 mock 服务端一致。
 */
function startMockServer(modelsBehavior) {
  const sockets = new Set();
  const server = net.createServer((socket) => {
    sockets.add(socket);
    socket.on("close", () => sockets.delete(socket));
    let handshaken = false;
    let index = -1;
    let buffer = Buffer.alloc(0);

    socket.on("data", async (chunk) => {
      buffer = Buffer.concat([buffer, chunk]);
      if (!handshaken) {
        const headerEnd = buffer.indexOf("\r\n\r\n");
        if (headerEnd === -1) return;
        const header = buffer.subarray(0, headerEnd).toString("utf8");
        buffer = buffer.subarray(headerEnd + 4);
        const key = /Sec-WebSocket-Key: (.+)/.exec(header)?.[1]?.trim();
        const accept = crypto
          .createHash("sha1")
          .update(key + WEBSOCKET_GUID)
          .digest("base64");
        socket.write(
          [
            "HTTP/1.1 101 Switching Protocols",
            "Upgrade: websocket",
            "Connection: Upgrade",
            `Sec-WebSocket-Accept: ${accept}`,
            `Sec-WebSocket-Protocol: ${SUBPROTOCOL}`,
            "\r\n",
          ].join("\r\n"),
        );
        handshaken = true;
        index += 1;
      }
      while (handshaken) {
        const frame = decodeClientFrame(buffer);
        if (!frame) break;
        buffer = frame.rest;
        if (frame.opcode === 8) {
          socket.end(encodeServerFrame(8, Buffer.alloc(0)));
          return;
        }
        if (frame.opcode === 9) {
          socket.write(encodeServerFrame(10, frame.payload));
          continue;
        }
        if (frame.opcode !== 2) continue;
        const request = decode(frame.payload);
        const response = route(request);
        if (!response) continue;
        if (request.messageType === "AvailableModelsRequest") {
          if (modelsBehavior === MODELS_BEHAVIOR.answerThenDropFirst && index === 0) {
            socket.write(encodeServerFrame(2, encode(response)));
            socket.destroy();
            return;
          }
          if (modelsBehavior === MODELS_BEHAVIOR.dropWithoutAnswer) {
            socket.destroy();
            return;
          }
          if (modelsBehavior === MODELS_BEHAVIOR.silent) {
            continue;
          }
        }
        socket.write(encodeServerFrame(2, encode(response)));
      }
    });
  });

  return new Promise((resolve) => {
    server.listen(0, "127.0.0.1", () => {
      resolve({
        port: server.address().port,
        close: () => {
          // 半关闭的 socket 会挂住进程，强制清掉。
          for (const socket of sockets) socket.destroy();
          server.close();
        },
      });
    });
  });
}

async function waitFor(predicate, timeoutMs = 5000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (await predicate()) return true;
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
  return false;
}

test("session reconnects after server drop", async () => {
  const server = await startMockServer(MODELS_BEHAVIOR.answerThenDropFirst);
  const issued = [];
  const statuses = [];
  const session = createNanaLiveSession({
    port: server.port,
    identity: IDENTITY,
    onToken: (token) => issued.push(token),
    onStatus: (status) => statuses.push(status),
    retryDelay: 50,
    maxRetryDelay: 100,
    requestTimeout: 5000,
  });

  await session.connect();
  const first = await session.request("AvailableModelsRequest");
  assert.equal(first.data.models[0].modelID, "m-1");

  // 第一条连接在回答后被服务端断开；重连后再次查询应成功。
  const reconnected = await waitFor(async () => {
    try {
      const models = await session.request("AvailableModelsRequest");
      return models.data.models[0].modelID === "m-1";
    } catch {
      return false;
    }
  });
  assert.equal(reconnected, true, "重连后未能再次完成请求");
  await session.close();

  assert.deepEqual(issued, ["issued-token"]);
  assert.equal(statuses.filter((status) => status === "connected").length >= 2, true);
  assert.equal(statuses.includes("reconnecting"), true);
  assert.equal(statuses.at(-1), "disconnected");
  server.close();
});

test("session request timeout and not connected", async () => {
  const server = await startMockServer(MODELS_BEHAVIOR.silent);
  const session = createNanaLiveSession({
    port: server.port,
    identity: IDENTITY,
    retryDelay: 50,
    maxRetryDelay: 100,
    requestTimeout: 200,
  });

  await assert.rejects(session.request("AvailableModelsRequest"), /not_connected/);

  await session.connect();
  await assert.rejects(session.request("AvailableModelsRequest"), /request_timeout/);
  await session.close();

  await assert.rejects(session.request("AvailableModelsRequest"), /not_connected/);
  server.close();
});

test("session pending requests fail on drop", async () => {
  const server = await startMockServer(MODELS_BEHAVIOR.dropWithoutAnswer);
  const session = createNanaLiveSession({
    port: server.port,
    identity: IDENTITY,
    retryDelay: 50,
    maxRetryDelay: 100,
    requestTimeout: 5000,
  });

  await session.connect();
  await assert.rejects(session.request("AvailableModelsRequest"), /connection_lost/);
  await session.close();
  server.close();
});
