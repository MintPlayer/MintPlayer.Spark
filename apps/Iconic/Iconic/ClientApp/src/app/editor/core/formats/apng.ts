import { chunk, concat, filterScanlines, ihdr, PNG_SIGNATURE, zlib, type Rgba } from './png';

/**
 * Animated PNG (D9): full-frame, full-alpha frames. The first frame is the IDAT (what a reader without APNG support
 * shows); each later one is an fcTL + fdAT pair. Every frame replaces the canvas (dispose NONE, blend SOURCE), so
 * transparent pixels stay transparent instead of piling up.
 */
export async function encodeApng(frames: Rgba[], options: { delayMs: number; loops: number }): Promise<Uint8Array> {
  if (frames.length === 0) throw new Error('No frames.');
  const { width, height } = frames[0];
  const actl = new Uint8Array(8);
  new DataView(actl.buffer).setUint32(0, frames.length);
  new DataView(actl.buffer).setUint32(4, options.loops);
  const parts: Uint8Array[] = [PNG_SIGNATURE, ihdr(width, height), chunk('acTL', actl)];
  let seq = 0;
  // The delay as a fraction: milliseconds over 1000, reduced to fit 16 bits.
  const num = Math.max(1, Math.round(options.delayMs)), den = 1000;
  for (let i = 0; i < frames.length; i++) {
    const fctl = new Uint8Array(26);
    const v = new DataView(fctl.buffer);
    v.setUint32(0, seq++);
    v.setUint32(4, width);
    v.setUint32(8, height);
    v.setUint16(20, num);
    v.setUint16(22, den);
    fctl[24] = 0; // APNG_DISPOSE_OP_NONE
    fctl[25] = 0; // APNG_BLEND_OP_SOURCE
    parts.push(chunk('fcTL', fctl));
    const data = await zlib(filterScanlines(frames[i]));
    if (i === 0) parts.push(chunk('IDAT', data));
    else {
      const fdat = new Uint8Array(4 + data.length);
      new DataView(fdat.buffer).setUint32(0, seq++);
      fdat.set(data, 4);
      parts.push(chunk('fdAT', fdat));
    }
  }
  parts.push(chunk('IEND', new Uint8Array(0)));
  return concat(parts);
}
