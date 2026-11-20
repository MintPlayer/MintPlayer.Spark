import { evaluate } from './evaluate';
import { encodeAni } from './formats/ani';
import { encodeApng } from './formats/apng';
import { encodeGif } from './formats/gif';
import { encodeIcon, type IconEntry } from './formats/ico';
import { encodePng, type Rgba } from './formats/png';
import { isWebp, muxAnimatedWebp } from './formats/webp';
import type { Id, Output, Scene } from './model';
import { enabledOutputs, renderToImageData, type PaintSource } from './render';
import type { TextOutliner } from './shapes';
import { sceneToSvg } from './svg';

/** What exports need besides the scene: the fonts and the painted bitmaps. */
export interface ExportContext {
  text?: TextOutliner;
  paint?: PaintSource;
  paintDataUrl?: (layer: Id, width: number) => string | null;
}

export type ExportFormat = 'ico' | 'cur' | 'ani' | 'png' | 'svg' | 'gif' | 'apng' | 'webp';

export interface ExportFile {
  name: string;
  type: string;
  bytes: Uint8Array | string;
}

function frameImage(scene: Scene, output: Output, frame: number | null, ctx: ExportContext): Rgba {
  const { positions } = evaluate(scene, { frame, text: ctx.text });
  const img = renderToImageData(scene, { width: output.width, height: output.height, output, positions, frame, paint: ctx.paint, text: ctx.text });
  return { width: img.width, height: img.height, data: img.data };
}

function hotspotFor(scene: Scene, output: Output): { x: number; y: number } {
  return {
    x: Math.min(output.width - 1, Math.max(0, Math.round((scene.hotspot.x * output.width) / scene.width))),
    y: Math.min(output.height - 1, Math.max(0, Math.round((scene.hotspot.y * output.height) / scene.height))),
  };
}

/** One output for single-size formats: the one asked for, else the largest enabled. */
export function pickOutput(scene: Scene, outputId?: Id): Output {
  if (outputId && scene.outputs[outputId]) return scene.outputs[outputId];
  const all = enabledOutputs(scene).map(o => o.output);
  return all.sort((a, b) => b.width - a.width)[0] ?? { width: scene.width, height: scene.height, strokeMode: 'scale', minStroke: 1, encoding: 'png', enabled: true };
}

const base = (name: string) => (name.trim() || 'icon').replace(/[\\/:*?"<>|]+/g, '-');

/**
 * Renders the scene into one file. ICO and CUR hold every enabled output (D1); the other formats render one output
 * (`outputId`) at its size. Animated formats render every frame from the rest pose (D19).
 */
export async function exportScene(scene: Scene, format: ExportFormat, name: string, ctx: ExportContext, outputId?: Id): Promise<ExportFile> {
  const outputs = enabledOutputs(scene).map(o => o.output);
  if ((format === 'ico' || format === 'cur' || format === 'ani') && outputs.length === 0) throw new Error('Add at least one output size first.');
  const delayMs = 1000 / scene.animation.fps;
  const frames = Array.from({ length: Math.max(1, scene.animation.frames) }, (_, i) => i);
  switch (format) {
    case 'ico':
    case 'cur': {
      const entries: IconEntry[] = outputs
        .filter(o => o.width <= 256 && o.height <= 256)
        .map(o => ({ image: frameImage(scene, o, null, ctx), encoding: o.encoding, hotspot: format === 'cur' ? hotspotFor(scene, o) : undefined }));
      return { name: `${base(name)}.${format}`, type: format === 'ico' ? 'image/x-icon' : 'image/x-win-bitmap', bytes: await encodeIcon(entries, format) };
    }
    case 'ani': {
      const sizes = outputs.filter(o => o.width <= 256);
      const curs: Uint8Array[] = [];
      for (const f of frames) {
        curs.push(await encodeIcon(sizes.map(o => ({ image: frameImage(scene, o, f, ctx), encoding: o.encoding, hotspot: hotspotFor(scene, o) })), 'cur'));
      }
      const largest = sizes.reduce((a, b) => (b.width > a.width ? b : a), sizes[0]);
      return { name: `${base(name)}.ani`, type: 'application/x-navi-animation', bytes: encodeAni(curs, { jiffies: 60 / scene.animation.fps, width: largest.width, height: largest.height, title: name }) };
    }
    case 'png': {
      const o = pickOutput(scene, outputId);
      return { name: `${base(name)}-${o.width}.png`, type: 'image/png', bytes: await encodePng(frameImage(scene, o, null, ctx)) };
    }
    case 'svg': {
      const { positions } = evaluate(scene, { frame: null, text: ctx.text });
      return { name: `${base(name)}.svg`, type: 'image/svg+xml', bytes: sceneToSvg(scene, positions, { text: ctx.text, paintDataUrl: ctx.paintDataUrl }) };
    }
    case 'gif': {
      const o = pickOutput(scene, outputId);
      const images = frames.map(f => frameImage(scene, o, f, ctx));
      return { name: `${base(name)}-${o.width}.gif`, type: 'image/gif', bytes: encodeGif(images, { delayMs, loop: scene.animation.loop, alphaThreshold: scene.animation.alphaThreshold, matte: scene.animation.matte }) };
    }
    case 'apng': {
      const o = pickOutput(scene, outputId);
      const images = frames.map(f => frameImage(scene, o, f, ctx));
      return { name: `${base(name)}-${o.width}.png`, type: 'image/apng', bytes: await encodeApng(images, { delayMs, loops: scene.animation.loop ? 0 : 1 }) };
    }
    case 'webp': {
      const o = pickOutput(scene, outputId);
      const stills: Uint8Array[] = [];
      for (const f of frames) stills.push(await encodeWebpStill(frameImage(scene, o, f, ctx)));
      return { name: `${base(name)}-${o.width}.webp`, type: 'image/webp', bytes: muxAnimatedWebp(stills, { width: o.width, height: o.height, delayMs, loops: scene.animation.loop ? 0 : 1 }) };
    }
  }
}

/** The browser's WebP encoder, lossless where it offers it. Throws where the browser cannot encode WebP. */
export async function encodeWebpStill(img: Rgba): Promise<Uint8Array> {
  const canvas = document.createElement('canvas');
  canvas.width = img.width;
  canvas.height = img.height;
  canvas.getContext('2d')!.putImageData(new ImageData(new Uint8ClampedArray(img.data), img.width, img.height), 0, 0);
  const blob = await new Promise<Blob | null>(resolve => canvas.toBlob(resolve, 'image/webp', 1));
  const bytes = blob ? new Uint8Array(await blob.arrayBuffer()) : new Uint8Array(0);
  if (!isWebp(bytes)) throw new Error('This browser cannot write WebP. Use APNG or GIF instead.');
  return bytes;
}

/** Whether the browser can encode WebP (Safari cannot): decides whether the option shows. */
export async function canEncodeWebp(): Promise<boolean> {
  try {
    await encodeWebpStill({ width: 1, height: 1, data: new Uint8Array([0, 0, 0, 0]) });
    return true;
  } catch {
    return false;
  }
}

export function download(file: ExportFile): void {
  const blob = new Blob([file.bytes as BlobPart], { type: file.type });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = file.name;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10_000);
}
