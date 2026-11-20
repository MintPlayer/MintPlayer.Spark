import { applyPalette, GIFEncoder, quantize } from 'gifenc';
import type { Rgba } from './png';

/**
 * Animated GIF (D9) through gifenc (MIT). GIF has one bit of transparency, so each pixel is either cut (alpha under
 * the threshold) or blended onto the matte colour and kept opaque. One palette for the whole animation (255 colours
 * plus the transparent entry) keeps colours from flickering between frames.
 */
export function encodeGif(frames: Rgba[], options: { delayMs: number; loop: boolean; alphaThreshold: number; matte: string }): Uint8Array {
  if (frames.length === 0) throw new Error('No frames.');
  const { width, height } = frames[0];
  const matte = parseHex(options.matte);
  const prepared = frames.map(f => flatten(f, options.alphaThreshold, matte));

  // Sample every frame's opaque pixels for one global palette (every other frame when there are many).
  const step = Math.max(1, Math.ceil(prepared.length / 24));
  // Cut pixels stay out of the sample, or the transparent areas would spend palette entries on black.
  const sampled = prepared.filter((_, i) => i % step === 0);
  const sample = new Uint8Array(sampled.reduce((n, p) => n + (p.cut.length - p.cut.reduce((a, b) => a + b, 0)) * 4, 0) || 4);
  let o = 0;
  for (const p of sampled) {
    for (let i = 0; i < p.cut.length; i++) {
      if (p.cut[i]) continue;
      sample.set(p.rgba.subarray(i * 4, i * 4 + 4), o);
      o += 4;
    }
  }
  const palette = quantize(sample, 255, { format: 'rgb565' }) as number[][];
  const transparentIndex = palette.length;
  palette.push([0, 0, 0]);

  const gif = GIFEncoder();
  for (const p of prepared) {
    const index = applyPalette(p.rgba, palette.slice(0, transparentIndex), 'rgb565');
    for (let i = 0; i < p.cut.length; i++) if (p.cut[i]) index[i] = transparentIndex;
    gif.writeFrame(index, width, height, {
      palette,
      delay: options.delayMs,
      repeat: options.loop ? 0 : -1,
      transparent: true,
      transparentIndex,
      // Restore to background: a transparent pixel shows through to the page, not to the previous frame.
      dispose: 2,
    });
  }
  gif.finish();
  return gif.bytes();
}

function flatten(img: Rgba, threshold: number, matte: [number, number, number]): { rgba: Uint8Array; cut: Uint8Array } {
  const n = img.width * img.height;
  const rgba = new Uint8Array(n * 4);
  const cut = new Uint8Array(n);
  for (let i = 0; i < n; i++) {
    const a = img.data[i * 4 + 3];
    if (a < threshold) { cut[i] = 1; continue; }
    const k = a / 255;
    for (let c = 0; c < 3; c++) rgba[i * 4 + c] = Math.round(img.data[i * 4 + c] * k + matte[c] * (1 - k));
    rgba[i * 4 + 3] = 255;
  }
  return { rgba, cut };
}

export function parseHex(hex: string): [number, number, number] {
  const m = /^#?([0-9a-f]{6})$/i.exec(hex.trim());
  const v = m ? parseInt(m[1], 16) : 0xffffff;
  return [(v >> 16) & 0xff, (v >> 8) & 0xff, v & 0xff];
}
