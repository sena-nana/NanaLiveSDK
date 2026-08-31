import assert from "node:assert/strict";
import test from "node:test";

import { encode, decode } from "./msgpack.mjs";

test("encode/decode roundtrip of protocol envelope", () => {
  const envelope = {
    apiName: "NanaLiveControlAPI",
    apiVersion: "2.0",
    requestID: "nanalive-1",
    messageType: "AvailableModelsRequest",
    data: { nested: [1, -2.5, true, null, "文本"] },
  };
  assert.deepEqual(decode(encode(envelope)), envelope);
});

test("decode filters prototype-polluting keys", () => {
  const payload = {};
  Object.defineProperty(payload, "__proto__", { value: { polluted: true }, enumerable: true });
  payload.constructor = { stolen: true };
  payload.prototype = { stolen: true };
  payload.safe = 1;

  const decoded = decode(encode(payload));
  assert.equal({}.polluted, undefined);
  assert.deepEqual(decoded, { safe: 1 });
});

test("decode rejects trailing bytes", () => {
  const bytes = encode({ a: 1 });
  const padded = new Uint8Array(bytes.length + 1);
  padded.set(bytes);
  padded[bytes.length] = 0xc0;
  assert.throws(() => decode(padded), /trailing_msgpack_bytes/);
});

test("decode rejects truncated values", () => {
  // fixstr 声明 5 字节但只有 2 字节。
  assert.throws(() => decode(new Uint8Array([0xa5, 0x68, 0x69])), /truncated_msgpack/);
  // array16 声明 3 个元素但流已尽。
  assert.throws(() => decode(new Uint8Array([0x93, 0x01])), /truncated_msgpack/);
});

test("decode rejects excessive nesting", () => {
  let value = [];
  for (let index = 0; index < 200; index += 1) value = [value];
  assert.throws(() => decode(encode(value)), /msgpack_depth_overflow/);
});

test("encode rejects unsupported values instead of writing zero bytes", () => {
  assert.throws(() => encode({ callback: () => {} }), /unsupported_msgpack_value/);
  assert.throws(() => encode(1n), /unsupported_msgpack_value/);
  assert.throws(() => encode(Symbol("x")), /unsupported_msgpack_value/);
});
