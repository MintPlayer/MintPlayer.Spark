import { addLayer } from './edit';
import { decodeIcon } from './formats/ico';
import { decodePng, encodePng, PNG_SIGNATURE, type Rgba } from './formats/png';
import { newId, newOutput, type Id, type Scene } from './model';

/**
 * Import (D9): an ICO or CUR becomes one paint layer with a bitmap per size it holds, and a PNG becomes a paint
 * layer at its width. Sizes the scene has no output for get one, so every imported image shows up in an export.
 */
export async function importImage(scene: Scene, name: string, bytes: Uint8Array): Promise<{ layer: Id; sizes: number[] }> {
  const images: (Rgba & { hotspot?: { x: number; y: number } })[] = [];
  if (PNG_SIGNATURE.every((b, i) => bytes[i] === b)) images.push(await decodeOrBrowser(bytes));
  else if (bytes[0] === 0 && bytes[1] === 0 && (bytes[2] === 1 || bytes[2] === 2)) {
    const icon = await decodeIcon(bytes);
    images.push(...icon.images);
    const cursor = icon.images.find(i => i.hotspot);
    if (icon.kind === 'cur' && cursor?.hotspot) {
      scene.hotspot = { x: (cursor.hotspot.x * scene.width) / cursor.width, y: (cursor.hotspot.y * scene.height) / cursor.height };
    }
  } else throw new Error('Iconic imports .ico, .cur and .png files.');

  const layer = addLayer(scene, 'paint', name.replace(/\.[^.]+$/, '') || 'Imported');
  const sizes: number[] = [];
  // Keep the largest copy of each width (a file may hold the same size at several colour depths).
  const byWidth = new Map<number, Rgba>();
  for (const img of images) if (!byWidth.has(img.width) || img.height === img.width) byWidth.set(img.width, img);
  for (const [width, img] of byWidth) {
    scene.layers[layer].bitmaps[String(width)] = await toDataUrl(img);
    sizes.push(width);
    const hasOutput = scene.outputOrder.some(id => scene.outputs[id]?.width === width);
    if (!hasOutput) {
      const id = newId('o');
      scene.outputs[id] = { ...newOutput(width), height: img.height };
      scene.outputOrder.push(id);
      scene.outputOrder.sort((a, b) => scene.outputs[a].width - scene.outputs[b].width);
    }
  }
  return { layer, sizes: sizes.sort((a, b) => a - b) };
}

/** Our own decoder reads 8-bit RGB(A); anything else (palette, 16-bit, interlaced) goes through the browser. */
async function decodeOrBrowser(bytes: Uint8Array): Promise<Rgba> {
  const colorType = bytes[25], depth = bytes[24], interlace = bytes[28];
  if (depth === 8 && (colorType === 6 || colorType === 2) && interlace === 0) return decodePng(bytes);
  const bitmap = await createImageBitmap(new Blob([bytes as BlobPart], { type: 'image/png' }));
  const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
  const ctx = canvas.getContext('2d')!;
  ctx.drawImage(bitmap, 0, 0);
  const data = ctx.getImageData(0, 0, bitmap.width, bitmap.height);
  return { width: data.width, height: data.height, data: data.data };
}

export async function toDataUrl(img: Rgba): Promise<string> {
  const png = await encodePng(img);
  let binary = '';
  for (let i = 0; i < png.length; i += 0x8000) binary += String.fromCharCode(...png.subarray(i, i + 0x8000));
  return `data:image/png;base64,${btoa(binary)}`;
}

export function dataUrlBytes(url: string): Uint8Array {
  const base64 = url.slice(url.indexOf(',') + 1);
  const binary = atob(base64);
  const out = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) out[i] = binary.charCodeAt(i);
  return out;
}
