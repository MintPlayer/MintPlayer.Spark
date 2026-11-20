import { parse, type Font } from 'opentype.js';
import { vec, type PathCommand, type Vec } from './geometry';
import type { Id, TextShape } from './model';
import type { TextOutliner } from './shapes';

/** The font a text shape uses when it names none: Inter (OFL-1.1), served from /fonts. */
export const DEFAULT_FONT = 'default';
export const DEFAULT_FONT_URL = '/fonts/inter-latin-400-normal.woff';

/**
 * Live text (D23): glyph outlines from opentype.js, so the editor, SVG and every bitmap size draw the same curves
 * and nothing depends on fonts installed on the viewer's machine. Fonts load asynchronously; `changed` fires when one
 * arrives so the editor can redraw.
 */
export class FontLibrary implements TextOutliner {
  private readonly fonts = new Map<Id, Font>();
  private readonly loading = new Map<Id, Promise<Font | null>>();

  constructor(private readonly changed: () => void = () => {}) {}

  has(id: Id): boolean {
    return this.fonts.has(id);
  }

  add(id: Id, bytes: ArrayBuffer): Font {
    const font = parse(bytes);
    this.fonts.set(id, font);
    this.changed();
    return font;
  }

  /** Loads a font from a URL once; later calls share the same request. */
  load(id: Id, url: string, fetcher: (url: string) => Promise<ArrayBuffer> = defaultFetch): Promise<Font | null> {
    const existing = this.loading.get(id);
    if (existing) return existing;
    const promise = fetcher(url)
      .then(bytes => this.add(id, bytes))
      .catch(() => null);
    this.loading.set(id, promise);
    return promise;
  }

  familyName(id: Id): string | null {
    const font = this.fonts.get(id);
    return font ? font.names.fontFamily?.['en'] ?? Object.values(font.names.fontFamily ?? {})[0] ?? null : null;
  }

  outline(shape: TextShape, anchor: Vec): PathCommand[] | null {
    const font = this.fonts.get(shape.font ?? DEFAULT_FONT) ?? this.fonts.get(DEFAULT_FONT);
    if (!font || !shape.text) return font ? [] : null;
    const lineHeight = shape.size * 1.2;
    const out: PathCommand[] = [];
    shape.text.split('\n').forEach((line, i) => {
      const width = font.getAdvanceWidth(line, shape.size);
      const x = shape.align === 'center' ? anchor.x - width / 2 : shape.align === 'right' ? anchor.x - width : anchor.x;
      // The anchor is the first line's baseline.
      const path = font.getPath(line, x, anchor.y + i * lineHeight, shape.size);
      let last = vec(x, anchor.y);
      for (const cmd of path.commands) {
        switch (cmd.type) {
          case 'M': last = vec(cmd.x, cmd.y); out.push({ c: 'M', p: last }); break;
          case 'L': last = vec(cmd.x, cmd.y); out.push({ c: 'L', p: last }); break;
          case 'C': last = vec(cmd.x, cmd.y); out.push({ c: 'C', c1: vec(cmd.x1, cmd.y1), c2: vec(cmd.x2, cmd.y2), p: last }); break;
          case 'Q': {
            // A quadratic as the equivalent cubic.
            const q = vec(cmd.x1, cmd.y1), p = vec(cmd.x, cmd.y);
            out.push({ c: 'C', c1: vec(last.x + (2 / 3) * (q.x - last.x), last.y + (2 / 3) * (q.y - last.y)), c2: vec(p.x + (2 / 3) * (q.x - p.x), p.y + (2 / 3) * (q.y - p.y)), p });
            last = p;
            break;
          }
          case 'Z': out.push({ c: 'Z' }); break;
        }
      }
    });
    return out;
  }
}

async function defaultFetch(url: string): Promise<ArrayBuffer> {
  const response = await fetch(url, { credentials: 'same-origin' });
  if (!response.ok) throw new Error(`Font ${url}: ${response.status}`);
  return response.arrayBuffer();
}
