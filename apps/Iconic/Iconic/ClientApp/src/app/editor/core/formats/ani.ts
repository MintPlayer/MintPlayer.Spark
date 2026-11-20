import { concat } from './png';

/**
 * Animated cursors (.ani, D9): a RIFF 'ACON' with an 'anih' header, an optional 'rate' (per-step display time in
 * jiffies, 1/60 s), and a LIST 'fram' of 'icon' chunks, each a complete .cur file.
 */
export function encodeAni(frames: Uint8Array[], options: { jiffies: number; width: number; height: number; title?: string }): Uint8Array {
  const n = frames.length;
  const anih = new Uint8Array(36);
  const v = new DataView(anih.buffer);
  v.setUint32(0, 36, true); // cbSize
  v.setUint32(4, n, true); // frames
  v.setUint32(8, n, true); // steps
  v.setUint32(12, options.width, true);
  v.setUint32(16, options.height, true);
  v.setUint32(20, 32, true); // bit count
  v.setUint32(24, 1, true); // planes
  v.setUint32(28, Math.max(1, Math.round(options.jiffies)), true); // default rate
  v.setUint32(32, 1, true); // AF_ICON: frames are icon/cursor files

  const parts: Uint8Array[] = [];
  if (options.title) {
    const text = new TextEncoder().encode(options.title + '\0');
    parts.push(list('INFO', [riffChunk('INAM', text)]));
  }
  parts.push(riffChunk('anih', anih));
  parts.push(list('fram', frames.map(f => riffChunk('icon', f))));
  return riff('ACON', parts);
}

export function fourcc(s: string): Uint8Array {
  return new Uint8Array([...s].map(c => c.charCodeAt(0)));
}

export function riffChunk(id: string, data: Uint8Array): Uint8Array {
  const pad = data.length & 1;
  const out = new Uint8Array(8 + data.length + pad);
  out.set(fourcc(id), 0);
  new DataView(out.buffer).setUint32(4, data.length, true);
  out.set(data, 8);
  return out;
}

export function list(type: string, chunks: Uint8Array[]): Uint8Array {
  return riffChunk('LIST', concat([fourcc(type), ...chunks]));
}

export function riff(form: string, chunks: Uint8Array[]): Uint8Array {
  const body = concat([fourcc(form), ...chunks]);
  const out = new Uint8Array(8 + body.length);
  out.set(fourcc('RIFF'), 0);
  new DataView(out.buffer).setUint32(4, body.length, true);
  out.set(body, 8);
  return out;
}
