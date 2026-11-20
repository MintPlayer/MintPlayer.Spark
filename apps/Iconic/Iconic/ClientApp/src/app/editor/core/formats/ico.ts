import { concat, decodePng, encodePng, PNG_SIGNATURE, type Rgba } from './png';

/**
 * ICO and CUR files (docs/iconic_PRD.md §2.3): a 6-byte ICONDIR, a 16-byte entry per image, then the images. An
 * entry is a PNG (Vista and later; required at 256 px to stay small) or a classic DIB: a BITMAPINFOHEADER with the
 * height doubled, bottom-up 32-bit BGRA rows, then a 1-bit AND mask padded to 4 bytes per row. A CUR stores the
 * hotspot where an ICO stores colour planes and bit count.
 */

export interface IconEntry {
  image: Rgba;
  encoding: 'png' | 'bmp';
  /** CUR only: the hotspot in this image's pixels. */
  hotspot?: { x: number; y: number };
}

/** The DIB form of an entry: header, XOR (BGRA) bitmap bottom-up, AND mask bottom-up. */
export function encodeDib(img: Rgba): Uint8Array {
  const { width, height, data } = img;
  const maskStride = Math.ceil(width / 32) * 4;
  const out = new Uint8Array(40 + width * height * 4 + maskStride * height);
  const v = new DataView(out.buffer);
  v.setUint32(0, 40, true);
  v.setInt32(4, width, true);
  v.setInt32(8, height * 2, true);
  v.setUint16(12, 1, true);
  v.setUint16(14, 32, true);
  v.setUint32(20, width * height * 4 + maskStride * height, true);
  let o = 40;
  for (let y = height - 1; y >= 0; y--) {
    for (let x = 0; x < width; x++) {
      const i = (y * width + x) * 4;
      out[o++] = data[i + 2]; out[o++] = data[i + 1]; out[o++] = data[i]; out[o++] = data[i + 3];
    }
  }
  // The AND mask: 1 = transparent. Readers that ignore alpha (old shells) use it.
  for (let y = height - 1; y >= 0; y--) {
    for (let x = 0; x < width; x++) {
      if (data[(y * width + x) * 4 + 3] < 128) out[o + (x >> 3)] |= 0x80 >> (x & 7);
    }
    o += maskStride;
  }
  return out;
}

export async function encodeIcon(entries: IconEntry[], kind: 'ico' | 'cur' = 'ico'): Promise<Uint8Array> {
  const sorted = [...entries].sort((a, b) => a.image.width - b.image.width);
  const images = await Promise.all(sorted.map(e => (e.encoding === 'png' || e.image.width >= 256 ? encodePng(e.image) : Promise.resolve(encodeDib(e.image)))));
  const header = new Uint8Array(6 + 16 * sorted.length);
  const v = new DataView(header.buffer);
  v.setUint16(2, kind === 'ico' ? 1 : 2, true);
  v.setUint16(4, sorted.length, true);
  let offset = header.length;
  sorted.forEach((e, i) => {
    const o = 6 + 16 * i;
    header[o] = e.image.width >= 256 ? 0 : e.image.width;
    header[o + 1] = e.image.height >= 256 ? 0 : e.image.height;
    if (kind === 'ico') {
      v.setUint16(o + 4, 1, true);
      v.setUint16(o + 6, 32, true);
    } else {
      v.setUint16(o + 4, Math.round(e.hotspot?.x ?? 0), true);
      v.setUint16(o + 6, Math.round(e.hotspot?.y ?? 0), true);
    }
    v.setUint32(o + 8, images[i].length, true);
    v.setUint32(o + 12, offset, true);
    offset += images[i].length;
  });
  return concat([header, ...images]);
}

export interface DecodedIcon {
  kind: 'ico' | 'cur';
  images: (Rgba & { hotspot?: { x: number; y: number } })[];
}

/** Reads an ICO or CUR (import, §6 Stage 2): PNG entries and 32/24/8/4/1-bit DIB entries. */
export async function decodeIcon(bytes: Uint8Array): Promise<DecodedIcon> {
  const v = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const type = v.getUint16(2, true);
  if (v.getUint16(0, true) !== 0 || (type !== 1 && type !== 2)) throw new Error('Not an ICO or CUR file.');
  const count = v.getUint16(4, true);
  const images: DecodedIcon['images'] = [];
  for (let i = 0; i < count; i++) {
    const o = 6 + 16 * i;
    const size = v.getUint32(o + 8, true), offset = v.getUint32(o + 12, true);
    const data = bytes.subarray(offset, offset + size);
    const isPng = PNG_SIGNATURE.every((b, k) => data[k] === b);
    const image = isPng ? await decodePng(data) : decodeDib(data);
    images.push(type === 2 ? { ...image, hotspot: { x: v.getUint16(o + 4, true), y: v.getUint16(o + 6, true) } } : image);
  }
  return { kind: type === 1 ? 'ico' : 'cur', images };
}

export function decodeDib(data: Uint8Array): Rgba {
  const v = new DataView(data.buffer, data.byteOffset, data.byteLength);
  const headerSize = v.getUint32(0, true);
  const width = v.getInt32(4, true);
  const height = Math.abs(v.getInt32(8, true)) / 2;
  const bpp = v.getUint16(14, true);
  const colors = bpp <= 8 ? v.getUint32(32, true) || 1 << bpp : 0;
  const palette = headerSize;
  const pixels = palette + colors * 4;
  const stride = Math.ceil((width * bpp) / 32) * 4;
  const maskStride = Math.ceil(width / 32) * 4;
  const mask = pixels + stride * height;
  const out = new Uint8Array(width * height * 4);
  let anyAlpha = false;
  for (let y = 0; y < height; y++) {
    const row = pixels + (height - 1 - y) * stride;
    for (let x = 0; x < width; x++) {
      const i = (y * width + x) * 4;
      let r = 0, g = 0, b = 0, a = 255;
      if (bpp === 32 || bpp === 24) {
        const p = row + x * (bpp / 8);
        b = data[p]; g = data[p + 1]; r = data[p + 2];
        if (bpp === 32) { a = data[p + 3]; if (a) anyAlpha = true; }
      } else {
        const bit = x * bpp;
        const byte = data[row + (bit >> 3)];
        const index = (byte >> (8 - bpp - (bit & 7))) & ((1 << bpp) - 1);
        const c = palette + index * 4;
        b = data[c]; g = data[c + 1]; r = data[c + 2];
      }
      out[i] = r; out[i + 1] = g; out[i + 2] = b; out[i + 3] = a;
    }
  }
  // Without alpha in the colours, the AND mask says what is transparent.
  if (!anyAlpha && mask + maskStride * height <= data.length) {
    for (let y = 0; y < height; y++) {
      const row = mask + (height - 1 - y) * maskStride;
      for (let x = 0; x < width; x++) if (data[row + (x >> 3)] & (0x80 >> (x & 7))) out[(y * width + x) * 4 + 3] = 0;
    }
  } else if (bpp === 32 && !anyAlpha) {
    for (let i = 3; i < out.length; i += 4) out[i] = 255;
  }
  return { width, height, data: out };
}
