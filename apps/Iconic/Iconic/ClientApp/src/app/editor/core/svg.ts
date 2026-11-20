import { boundsOf, flatten, svgPathData } from './geometry';
import { layerOpacity } from './evaluate';
import { isClosed, type Id, type Scene } from './model';
import { pickPaintWidth } from './render';
import { shapePath, type Positions, type TextOutliner } from './shapes';

const esc = (s: string) => s.replace(/[&<>"']/g, ch => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[ch]!);
const num = (v: number) => (Math.round(v * 1000) / 1000).toString();

/**
 * The drawing as SVG at its design size. Shapes stay vectors (text as outlines, D23); a paint layer is embedded as its
 * largest bitmap, since it has no vector form. Guides are left out, as in every export.
 */
export function sceneToSvg(scene: Scene, positions: Positions, options: { text?: TextOutliner; paintDataUrl?: (layer: Id, width: number) => string | null; frame?: number | null } = {}): string {
  const defs: string[] = [];
  const body: string[] = [];
  let gradient = 0;
  for (const layerId of scene.layerOrder) {
    const layer = scene.layers[layerId];
    if (!layer?.visible) continue;
    const opacity = layerOpacity(scene, layerId, options.frame ?? null);
    const items: string[] = [];
    if (layer.kind === 'paint') {
      const width = pickPaintWidth(scene, layer.bitmaps, Number.MAX_SAFE_INTEGER);
      const href = width != null ? options.paintDataUrl?.(layerId, width) : null;
      if (href) items.push(`<image href="${esc(href)}" x="0" y="0" width="${scene.width}" height="${scene.height}" preserveAspectRatio="none" style="image-rendering:pixelated"/>`);
    } else {
      for (const id of layer.shapes) {
        const shape = scene.shapes[id];
        if (!shape || shape.hidden || shape.guide) continue;
        const path = shapePath(shape, positions, options.text);
        if (path.length === 0) continue;
        const attrs: string[] = [`d="${svgPathData(path)}"`];
        if (isClosed(shape) && shape.fill.type !== 'none') {
          const f = shape.fill;
          if (f.type === 'solid') attrs.push(`fill="${esc(f.color1)}"`);
          else {
            const gid = `g${gradient++}`;
            const b = boundsOf(flatten(path, 1).flatMap(s => s.points))!;
            const cx = (b.minX + b.maxX) / 2, cy = (b.minY + b.maxY) / 2;
            const stops = `<stop offset="0" stop-color="${esc(f.color1)}"/><stop offset="1" stop-color="${esc(f.color2)}"/>`;
            if (f.type === 'linear') {
              const a = (f.angle * Math.PI) / 180, dx = Math.cos(a), dy = Math.sin(a);
              const half = (Math.abs(dx) * (b.maxX - b.minX) + Math.abs(dy) * (b.maxY - b.minY)) / 2;
              defs.push(`<linearGradient id="${gid}" gradientUnits="userSpaceOnUse" x1="${num(cx - dx * half)}" y1="${num(cy - dy * half)}" x2="${num(cx + dx * half)}" y2="${num(cy + dy * half)}">${stops}</linearGradient>`);
            } else {
              const r = Math.max(b.maxX - b.minX, b.maxY - b.minY) / 2;
              defs.push(`<radialGradient id="${gid}" gradientUnits="userSpaceOnUse" cx="${num(cx)}" cy="${num(cy)}" r="${num(r)}">${stops}</radialGradient>`);
            }
            attrs.push(`fill="url(#${gid})"`);
          }
          if (f.opacity < 1) attrs.push(`fill-opacity="${num(f.opacity)}"`);
          attrs.push('fill-rule="evenodd"');
        } else {
          attrs.push('fill="none"');
        }
        const s = shape.stroke;
        if (s.width > 0) {
          attrs.push(`stroke="${esc(s.color)}"`, `stroke-width="${num(s.width)}"`, `stroke-linecap="${s.cap}"`, `stroke-linejoin="${s.join}"`);
          if (s.dash.length) attrs.push(`stroke-dasharray="${s.dash.map(num).join(' ')}"`);
          if (s.opacity < 1) attrs.push(`stroke-opacity="${num(s.opacity)}"`);
        }
        items.push(`<path ${attrs.join(' ')}/>`);
      }
    }
    if (items.length === 0) continue;
    body.push(opacity < 1 ? `<g opacity="${num(opacity)}">${items.join('')}</g>` : items.join(''));
  }
  const defsBlock = defs.length ? `<defs>${defs.join('')}</defs>` : '';
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${scene.width}" height="${scene.height}" viewBox="0 0 ${scene.width} ${scene.height}">${defsBlock}${body.join('')}</svg>`;
}
