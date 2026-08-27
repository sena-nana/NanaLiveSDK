const FLOAT64 = new Float64Array(1);
const FLOAT64_BYTES = new Uint8Array(FLOAT64.buffer);

export function encode(value) {
  const bytes = [];
  writeValue(bytes, value);
  return Uint8Array.from(bytes);
}

export function decode(input) {
  const bytes =
    input instanceof Uint8Array
      ? input
      : input instanceof ArrayBuffer
        ? new Uint8Array(input)
        : Uint8Array.from(input);
  const reader = { bytes, offset: 0 };
  return readValue(reader);
}

function writeValue(bytes, value) {
  if (value === null || value === undefined) {
    bytes.push(0xc0);
    return;
  }
  if (value === false) {
    bytes.push(0xc2);
    return;
  }
  if (value === true) {
    bytes.push(0xc3);
    return;
  }
  if (typeof value === "number") {
    writeNumber(bytes, value);
    return;
  }
  if (typeof value === "string") {
    writeString(bytes, value);
    return;
  }
  if (Array.isArray(value)) {
    writeArray(bytes, value);
    return;
  }
  if (typeof value === "object") {
    writeMap(bytes, value);
  }
}

function writeNumber(bytes, value) {
  if (Number.isInteger(value) && Number.isSafeInteger(value)) {
    writeInteger(bytes, value);
    return;
  }
  bytes.push(0xcb);
  FLOAT64[0] = value;
  bytes.push(
    FLOAT64_BYTES[7],
    FLOAT64_BYTES[6],
    FLOAT64_BYTES[5],
    FLOAT64_BYTES[4],
    FLOAT64_BYTES[3],
    FLOAT64_BYTES[2],
    FLOAT64_BYTES[1],
    FLOAT64_BYTES[0],
  );
}

function writeInteger(bytes, value) {
  if (value >= 0 && value <= 0x7f) {
    bytes.push(value);
    return;
  }
  if (value >= -32 && value < 0) {
    bytes.push(value & 0xff);
    return;
  }
  if (value >= 0 && value <= 0xff) {
    bytes.push(0xcc, value);
    return;
  }
  if (value >= 0 && value <= 0xffff) {
    bytes.push(0xcd, value >> 8, value & 0xff);
    return;
  }
  if (value >= 0 && value <= 0xffffffff) {
    bytes.push(0xce, (value >>> 24) & 0xff, (value >>> 16) & 0xff, (value >>> 8) & 0xff, value & 0xff);
    return;
  }
  if (value >= -0x80 && value < 0) {
    bytes.push(0xd0, value & 0xff);
    return;
  }
  if (value >= -0x8000 && value < 0) {
    bytes.push(0xd1, (value >> 8) & 0xff, value & 0xff);
    return;
  }
  if (value >= -0x80000000 && value < 0) {
    bytes.push(0xd2, (value >> 24) & 0xff, (value >> 16) & 0xff, (value >> 8) & 0xff, value & 0xff);
    return;
  }
  const hi = Math.floor(value / 0x100000000);
  const lo = value >>> 0;
  bytes.push(
    value >= 0 ? 0xcf : 0xd3,
    (hi >> 24) & 0xff,
    (hi >> 16) & 0xff,
    (hi >> 8) & 0xff,
    hi & 0xff,
    (lo >>> 24) & 0xff,
    (lo >>> 16) & 0xff,
    (lo >>> 8) & 0xff,
    lo & 0xff,
  );
}

function writeString(bytes, value) {
  const encoded = encoder().encode(value);
  if (encoded.length < 32) {
    bytes.push(0xa0 | encoded.length);
  } else if (encoded.length <= 0xff) {
    bytes.push(0xd9, encoded.length);
  } else if (encoded.length <= 0xffff) {
    bytes.push(0xda, encoded.length >> 8, encoded.length & 0xff);
  } else {
    bytes.push(
      0xdb,
      (encoded.length >>> 24) & 0xff,
      (encoded.length >>> 16) & 0xff,
      (encoded.length >>> 8) & 0xff,
      encoded.length & 0xff,
    );
  }
  for (const byte of encoded) bytes.push(byte);
}

function writeArray(bytes, value) {
  writeCollectionHeader(bytes, value.length, 0x90, 0xdc, 0xdd);
  for (const item of value) writeValue(bytes, item);
}

function writeMap(bytes, value) {
  const keys = Object.keys(value);
  writeCollectionHeader(bytes, keys.length, 0x80, 0xde, 0xdf);
  for (const key of keys) {
    writeString(bytes, key);
    writeValue(bytes, value[key]);
  }
}

function writeCollectionHeader(bytes, length, fix, u16, u32) {
  if (length < 16) {
    bytes.push(fix | length);
  } else if (length <= 0xffff) {
    bytes.push(u16, length >> 8, length & 0xff);
  } else {
    bytes.push(u32, (length >>> 24) & 0xff, (length >>> 16) & 0xff, (length >>> 8) & 0xff, length & 0xff);
  }
}

function readValue(reader) {
  const byte = readByte(reader);
  if (byte <= 0x7f) return byte;
  if (byte >= 0xe0) return byte - 256;
  if (byte >= 0xa0 && byte <= 0xbf) return readString(reader, byte - 0xa0);
  if (byte >= 0x90 && byte <= 0x9f) return readArray(reader, byte - 0x90);
  if (byte >= 0x80 && byte <= 0x8f) return readMap(reader, byte - 0x80);
  switch (byte) {
    case 0xc0:
      return null;
    case 0xc2:
      return false;
    case 0xc3:
      return true;
    case 0xcc:
      return readByte(reader);
    case 0xcd:
      return readUint(reader, 2);
    case 0xce:
      return readUint(reader, 4);
    case 0xcf:
      return readUint(reader, 8);
    case 0xd0:
      return sign(readByte(reader), 8);
    case 0xd1:
      return sign(readUint(reader, 2), 16);
    case 0xd2:
      return sign(readUint(reader, 4), 32);
    case 0xd3:
      return sign(readUint(reader, 8), 64);
    case 0xca:
      return readFloat32(reader);
    case 0xcb:
      return readFloat64(reader);
    case 0xd9:
      return readString(reader, readByte(reader));
    case 0xda:
      return readString(reader, readUint(reader, 2));
    case 0xdb:
      return readString(reader, readUint(reader, 4));
    case 0xdc:
      return readArray(reader, readUint(reader, 2));
    case 0xdd:
      return readArray(reader, readUint(reader, 4));
    case 0xde:
      return readMap(reader, readUint(reader, 2));
    case 0xdf:
      return readMap(reader, readUint(reader, 4));
    default:
      throw new Error("unsupported_msgpack");
  }
}

function readArray(reader, length) {
  const items = [];
  for (let index = 0; index < length; index += 1) {
    items.push(readValue(reader));
  }
  return items;
}

function readMap(reader, length) {
  const object = {};
  for (let index = 0; index < length; index += 1) {
    const key = readValue(reader);
    object[String(key)] = readValue(reader);
  }
  return object;
}

function readString(reader, length) {
  const slice = reader.bytes.subarray(reader.offset, reader.offset + length);
  reader.offset += length;
  return decoder().decode(slice);
}

function readByte(reader) {
  const value = reader.bytes[reader.offset];
  if (value === undefined) throw new Error("truncated_msgpack");
  reader.offset += 1;
  return value;
}

function readUint(reader, size) {
  let value = 0;
  for (let index = 0; index < size; index += 1) {
    value = value * 256 + readByte(reader);
  }
  return value;
}

function sign(value, bits) {
  const limit = 2 ** bits;
  return value >= limit / 2 ? value - limit : value;
}

function readFloat32(reader) {
  const view = new DataView(reader.bytes.buffer, reader.bytes.byteOffset + reader.offset, 4);
  reader.offset += 4;
  return view.getFloat32(0, false);
}

function readFloat64(reader) {
  const view = new DataView(reader.bytes.buffer, reader.bytes.byteOffset + reader.offset, 8);
  reader.offset += 8;
  return view.getFloat64(0, false);
}

let textEncoder;
let textDecoder;

function encoder() {
  textEncoder ??= new TextEncoder();
  return textEncoder;
}

function decoder() {
  textDecoder ??= new TextDecoder();
  return textDecoder;
}
