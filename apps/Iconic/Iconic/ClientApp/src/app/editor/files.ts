/** A data URL from bytes: how a draft keeps files (fonts, bitmaps) without a server (D3). */
export function toDataUrlBytes(bytes: Uint8Array, type: string): string {
  let binary = '';
  for (let i = 0; i < bytes.length; i += 0x8000) binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return `data:${type};base64,${btoa(binary)}`;
}

/** The MIME type a data URL declares. */
export function dataUrlType(url: string): string {
  return /^data:([^;,]+)/.exec(url)?.[1] ?? 'application/octet-stream';
}
