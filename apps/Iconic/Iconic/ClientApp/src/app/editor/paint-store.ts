import { signal } from '@angular/core';
import type { Id, Scene } from './core/model';
import type { PaintSource } from './core/render';

/**
 * The paint layers' bitmaps (D2): one canvas per layer and output width, loaded from the scene's `bitmaps` values (an
 * attachment name, or a data URL in a local draft) and painted on in place. `version` ticks whenever pixels change,
 * so the editor redraws.
 */
export class PaintStore implements PaintSource {
  readonly version = signal(0);
  private readonly canvases = new Map<string, HTMLCanvasElement>();
  private readonly loading = new Map<string, Promise<void>>();

  /** Turns an attachment name into a URL; data URLs pass through. */
  constructor(private resolve: (name: string) => string | null) {}

  setResolver(resolve: (name: string) => string | null): void {
    this.resolve = resolve;
  }

  /** Forgets every bitmap: after loading another project or restoring a snapshot. */
  reset(): void {
    this.canvases.clear();
    this.loading.clear();
    this.version.update(v => v + 1);
  }

  /** Loads every bitmap the scene names; resolves when they are all in (or failed). */
  loadAll(scene: Scene): Promise<void> {
    const pending: Promise<void>[] = [];
    for (const id of scene.layerOrder) {
      const layer = scene.layers[id];
      if (layer?.kind !== 'paint') continue;
      for (const [width, value] of Object.entries(layer.bitmaps)) pending.push(this.load(scene, id, Number(width), value));
    }
    return Promise.all(pending).then(() => undefined);
  }

  get(layer: Id, width: number): CanvasImageSource | null {
    return this.canvases.get(key(layer, width)) ?? null;
  }

  has(layer: Id, width: number): boolean {
    return this.canvases.has(key(layer, width));
  }

  /** The canvas to paint on, created empty when the layer has no bitmap at this width yet. */
  canvas(scene: Scene, layer: Id, width: number): HTMLCanvasElement {
    const k = key(layer, width);
    let c = this.canvases.get(k);
    if (!c) {
      c = document.createElement('canvas');
      c.width = width;
      c.height = heightFor(scene, width);
      this.canvases.set(k, c);
    }
    return c;
  }

  pixels(scene: Scene, layer: Id, width: number): ImageData {
    const c = this.canvas(scene, layer, width);
    return c.getContext('2d', { willReadFrequently: true })!.getImageData(0, 0, c.width, c.height);
  }

  putPixels(scene: Scene, layer: Id, width: number, data: ImageData): void {
    this.canvas(scene, layer, width).getContext('2d', { willReadFrequently: true })!.putImageData(data, 0, 0);
    this.touch();
  }

  touch(): void {
    this.version.update(v => v + 1);
  }

  private load(scene: Scene, layer: Id, width: number, value: string): Promise<void> {
    const k = key(layer, width);
    const existing = this.loading.get(k);
    if (existing) return existing;
    if (this.canvases.has(k)) return Promise.resolve();
    const url = value.startsWith('data:') ? value : this.resolve(value);
    if (!url) return Promise.resolve();
    const promise = new Promise<void>(resolve => {
      const img = new Image();
      img.crossOrigin = 'use-credentials';
      img.onload = () => {
        this.loading.delete(k);
        if (!this.canvases.has(k)) {
          const c = document.createElement('canvas');
          c.width = width;
          c.height = heightFor(scene, width);
          const ctx = c.getContext('2d', { willReadFrequently: true })!;
          ctx.imageSmoothingEnabled = false;
          ctx.drawImage(img, 0, 0, c.width, c.height);
          this.canvases.set(k, c);
          this.touch();
        }
        resolve();
      };
      img.onerror = () => { this.loading.delete(k); resolve(); };
      img.src = url;
    });
    this.loading.set(k, promise);
    return promise;
  }
}

function key(layer: Id, width: number): string {
  return `${layer}:${width}`;
}

/** An output's height for a width; otherwise the design's aspect ratio. */
export function heightFor(scene: Scene, width: number): number {
  const output = Object.values(scene.outputs).find(o => o.width === width);
  return output?.height ?? Math.max(1, Math.round((width * scene.height) / scene.width));
}

/** Flood fill from a pixel with a colour, matching the start pixel exactly (pixel-art fill). */
export function floodFill(img: ImageData, x: number, y: number, rgba: [number, number, number, number]): boolean {
  const { width, height, data } = img;
  if (x < 0 || y < 0 || x >= width || y >= height) return false;
  const at = (px: number, py: number) => (py * width + px) * 4;
  const s = at(x, y);
  const target = [data[s], data[s + 1], data[s + 2], data[s + 3]];
  if (target.every((v, i) => v === rgba[i])) return false;
  const same = (o: number) => data[o] === target[0] && data[o + 1] === target[1] && data[o + 2] === target[2] && data[o + 3] === target[3];
  const stack = [x, y];
  while (stack.length) {
    const py = stack.pop()!, px = stack.pop()!;
    let lx = px;
    while (lx > 0 && same(at(lx - 1, py))) lx--;
    let rx = px;
    while (rx < width - 1 && same(at(rx + 1, py))) rx++;
    for (let i = lx; i <= rx; i++) {
      const o = at(i, py);
      data[o] = rgba[0]; data[o + 1] = rgba[1]; data[o + 2] = rgba[2]; data[o + 3] = rgba[3];
      if (py > 0 && same(at(i, py - 1))) stack.push(i, py - 1);
      if (py < height - 1 && same(at(i, py + 1))) stack.push(i, py + 1);
    }
  }
  return true;
}

/** Draws a hard-edged line of pixels (Bresenham), for the pencil and the eraser at bitmap resolution. */
export function plotLine(x0: number, y0: number, x1: number, y1: number, plot: (x: number, y: number) => void): void {
  const dx = Math.abs(x1 - x0), dy = -Math.abs(y1 - y0);
  const sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
  let err = dx + dy;
  for (;;) {
    plot(x0, y0);
    if (x0 === x1 && y0 === y1) break;
    const e2 = 2 * err;
    if (e2 >= dy) { err += dy; x0 += sx; }
    if (e2 <= dx) { err += dx; y0 += sy; }
  }
}

export function hexToRgba(hex: string, alpha = 1): [number, number, number, number] {
  const m = /^#?([0-9a-f]{6})$/i.exec(hex.trim());
  const n = m ? parseInt(m[1], 16) : 0;
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255, Math.round(alpha * 255)];
}

export function rgbToHex(r: number, g: number, b: number): string {
  return `#${[r, g, b].map(v => v.toString(16).padStart(2, '0')).join('')}`;
}
