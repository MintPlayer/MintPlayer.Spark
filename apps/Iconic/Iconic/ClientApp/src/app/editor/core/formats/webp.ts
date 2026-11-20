import { concat } from './png';
import { fourcc, riff, riffChunk } from './ani';

/**
 * Animated WebP (D9) from still WebP frames. The browser encodes each frame (canvas.toBlob('image/webp')); this muxes
 * them: VP8X with the animation and alpha flags, ANIM (background, loops), and an ANMF per frame wrapping that frame's
 * ALPH/VP8/VP8L chunks. A browser that cannot encode WebP (it returns PNG instead) gets the option disabled.
 */
export function muxAnimatedWebp(stills: Uint8Array[], options: { width: number; height: number; delayMs: number; loops: number }): Uint8Array {
  const vp8x = new Uint8Array(10);
  vp8x[0] = 0x10 | 0x02; // alpha | animation
  put24(vp8x, 4, options.width - 1);
  put24(vp8x, 7, options.height - 1);
  const anim = new Uint8Array(6);
  // Background transparent; loop count.
  new DataView(anim.buffer).setUint16(4, options.loops, true);
  const frames = stills.map(still => {
    const body = imageChunks(still);
    const header = new Uint8Array(16);
    put24(header, 0, 0); // x / 2
    put24(header, 3, 0); // y / 2
    put24(header, 6, options.width - 1);
    put24(header, 9, options.height - 1);
    put24(header, 12, Math.max(1, Math.round(options.delayMs)));
    // Do not blend: each frame replaces the canvas; dispose: none.
    header[15] = 0x02;
    return riffChunk('ANMF', concat([header, body]));
  });
  return riff('WEBP', [riffChunk('VP8X', vp8x), riffChunk('ANIM', anim), ...frames]);
}

/** The ALPH, VP8 and VP8L chunks of a still WebP, as they appear in it (with headers and padding). */
export function imageChunks(webp: Uint8Array): Uint8Array {
  if (!isWebp(webp)) throw new Error('Not a WebP image.');
  const v = new DataView(webp.buffer, webp.byteOffset, webp.byteLength);
  const keep: Uint8Array[] = [];
  let o = 12;
  while (o + 8 <= webp.length) {
    const id = String.fromCharCode(...webp.subarray(o, o + 4));
    const size = v.getUint32(o + 4, true);
    const end = o + 8 + size + (size & 1);
    if (id === 'ALPH' || id === 'VP8 ' || id === 'VP8L') keep.push(webp.subarray(o, end));
    o = end;
  }
  return concat(keep);
}

export function isWebp(bytes: Uint8Array): boolean {
  const riffTag = fourcc('RIFF'), webpTag = fourcc('WEBP');
  return bytes.length > 12 && riffTag.every((b, i) => bytes[i] === b) && webpTag.every((b, i) => bytes[8 + i] === b);
}

function put24(target: Uint8Array, offset: number, value: number): void {
  target[offset] = value & 0xff;
  target[offset + 1] = (value >> 8) & 0xff;
  target[offset + 2] = (value >> 16) & 0xff;
}
