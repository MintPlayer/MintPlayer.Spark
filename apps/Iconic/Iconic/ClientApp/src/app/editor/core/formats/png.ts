/**
 * PNG writing with no canvas involved: RGBA in, bytes out. One deterministic encoder for ICO entries, the PNG export
 * and APNG frames (apng.ts), runnable in tests. Compression is the platform's zlib (CompressionStream 'deflate').
 */

export interface Rgba {
  width: number;
  height: number;
  /** width × height × 4 bytes, not premultiplied. */
  data: Uint8ClampedArray | Uint8Array;
}

const CRC_TABLE = (() => {
  const t = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    t[n] = c >>> 0;
  }
  return t;
})();

export function crc32(bytes: Uint8Array, crc = 0xffffffff): number {
  for (let i = 0; i < bytes.length; i++) crc = CRC_TABLE[(crc ^ bytes[i]) & 0xff] ^ (crc >>> 8);
  return crc;
}

export const PNG_SIGNATURE = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

export function chunk(type: string, data: Uint8Array): Uint8Array {
  const out = new Uint8Array(12 + data.length);
  const view = new DataView(out.buffer);
  view.setUint32(0, data.length);
  for (let i = 0; i < 4; i++) out[4 + i] = type.charCodeAt(i);
  out.set(data, 8);
  view.setUint32(8 + data.length, (crc32(out.subarray(4, 8 + data.length)) ^ 0xffffffff) >>> 0);
  return out;
}

export function concat(parts: Uint8Array[]): Uint8Array {
  const total = parts.reduce((n, p) => n + p.length, 0);
  const out = new Uint8Array(total);
  let o = 0;
  for (const p of parts) { out.set(p, o); o += p.length; }
  return out;
}

export function zlib(data: Uint8Array): Promise<Uint8Array> {
  return pipe(data, new CompressionStream('deflate'));
}

export function unzlib(data: Uint8Array): Promise<Uint8Array> {
  return pipe(data, new DecompressionStream('deflate'));
}

/** Through a (de)compression stream without Blob.stream(), which test DOMs lack. */
async function pipe(data: Uint8Array, transform: CompressionStream | DecompressionStream): Promise<Uint8Array> {
  const writer = transform.writable.getWriter();
  void writer.write(data as Uint8Array<ArrayBuffer>);
  void writer.close();
  return new Uint8Array(await new Response(transform.readable).arrayBuffer());
}

/** Filtered scanlines: per row the filter (None, Sub, Up, Average, Paeth) with the smallest sum of magnitudes. */
export function filterScanlines(img: Rgba): Uint8Array {
  const stride = img.width * 4;
  const out = new Uint8Array((stride + 1) * img.height);
  const candidates = Array.from({ length: 5 }, () => new Uint8Array(stride));
  for (let y = 0; y < img.height; y++) {
    const row = img.data.subarray(y * stride, (y + 1) * stride);
    const prev = y > 0 ? img.data.subarray((y - 1) * stride, y * stride) : null;
    let best = 0, bestScore = Infinity;
    for (let f = 0; f < 5; f++) {
      const c = candidates[f];
      let score = 0;
      for (let i = 0; i < stride; i++) {
        const a = i >= 4 ? row[i - 4] : 0;
        const b = prev ? prev[i] : 0;
        const cc = prev && i >= 4 ? prev[i - 4] : 0;
        let v: number;
        switch (f) {
          case 0: v = row[i]; break;
          case 1: v = row[i] - a; break;
          case 2: v = row[i] - b; break;
          case 3: v = row[i] - ((a + b) >> 1); break;
          default: {
            const p = a + b - cc, pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - cc);
            v = row[i] - (pa <= pb && pa <= pc ? a : pb <= pc ? b : cc);
          }
        }
        c[i] = v & 0xff;
        score += c[i] < 128 ? c[i] : 256 - c[i];
        if (score >= bestScore) break;
      }
      if (score < bestScore) { bestScore = score; best = f; }
    }
    // The early exit may have left the winner partly written: recompute it in full.
    out[y * (stride + 1)] = best;
    const dst = out.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1));
    for (let i = 0; i < stride; i++) {
      const a = i >= 4 ? row[i - 4] : 0;
      const b = prev ? prev[i] : 0;
      const cc = prev && i >= 4 ? prev[i - 4] : 0;
      let v: number;
      switch (best) {
        case 0: v = row[i]; break;
        case 1: v = row[i] - a; break;
        case 2: v = row[i] - b; break;
        case 3: v = row[i] - ((a + b) >> 1); break;
        default: {
          const p = a + b - cc, pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - cc);
          v = row[i] - (pa <= pb && pa <= pc ? a : pb <= pc ? b : cc);
        }
      }
      dst[i] = v & 0xff;
    }
  }
  return out;
}

export function ihdr(width: number, height: number): Uint8Array {
  const d = new Uint8Array(13);
  const v = new DataView(d.buffer);
  v.setUint32(0, width);
  v.setUint32(4, height);
  d[8] = 8; // bit depth
  d[9] = 6; // RGBA
  return chunk('IHDR', d);
}

export async function encodePng(img: Rgba): Promise<Uint8Array> {
  const idat = await zlib(filterScanlines(img));
  return concat([PNG_SIGNATURE, ihdr(img.width, img.height), chunk('IDAT', idat), chunk('IEND', new Uint8Array(0))]);
}

/** Reads an 8-bit RGBA or RGB PNG without a canvas (non-interlaced): enough for the tests to check our own output. */
export async function decodePng(bytes: Uint8Array): Promise<Rgba> {
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  let o = 8, width = 0, height = 0, colorType = 6;
  const idat: Uint8Array[] = [];
  while (o < bytes.length) {
    const length = view.getUint32(o);
    const type = String.fromCharCode(...bytes.subarray(o + 4, o + 8));
    const data = bytes.subarray(o + 8, o + 8 + length);
    if (type === 'IHDR') { width = view.getUint32(o + 8); height = view.getUint32(o + 12); colorType = data[9]; }
    if (type === 'IDAT') idat.push(data);
    if (type === 'IEND') break;
    o += 12 + length;
  }
  const channels = colorType === 2 ? 3 : 4;
  const raw = await unzlib(concat(idat));
  const stride = width * channels;
  const out = new Uint8Array(width * height * 4);
  const prev = new Uint8Array(stride), cur = new Uint8Array(stride);
  for (let y = 0; y < height; y++) {
    const f = raw[y * (stride + 1)];
    const src = raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1));
    for (let i = 0; i < stride; i++) {
      const a = i >= channels ? cur[i - channels] : 0, b = prev[i], c = i >= channels ? prev[i - channels] : 0;
      let p: number;
      switch (f) {
        case 0: p = 0; break;
        case 1: p = a; break;
        case 2: p = b; break;
        case 3: p = (a + b) >> 1; break;
        default: { const q = a + b - c, pa = Math.abs(q - a), pb = Math.abs(q - b), pc = Math.abs(q - c); p = pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }
      }
      cur[i] = (src[i] + p) & 0xff;
    }
    for (let x = 0; x < width; x++) {
      for (let k = 0; k < 3; k++) out[(y * width + x) * 4 + k] = cur[x * channels + k];
      out[(y * width + x) * 4 + 3] = channels === 4 ? cur[x * 4 + 3] : 255;
    }
    prev.set(cur);
  }
  return { width, height, data: out };
}
