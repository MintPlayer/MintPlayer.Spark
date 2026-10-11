/**
 * The commit graph's eight lane colours (#504, S9), one shade per theme. Each shade keeps at least 3:1
 * contrast (WCAG 2.x non-text contrast) against the card surface of its theme, Bootstrap's
 * `--bs-body-bg`: `#ffffff` in light, `#212529` in dark. `commit-graph-palette.spec.ts` computes it.
 *
 * The hues follow Bootstrap's palette (blue, green, red, purple, orange, cyan, pink, yellow); the light
 * shades are its 600–700 steps and the dark ones its 300–400 steps, because the base hues of orange,
 * cyan and yellow fall under 3:1 on white.
 */
export interface LaneShade {
  light: string;
  dark: string;
}

export const LANE_PALETTE: readonly LaneShade[] = [
  { light: '#0a58ca', dark: '#6ea8fe' }, // blue
  { light: '#146c43', dark: '#75b798' }, // green
  { light: '#b02a37', dark: '#ea868f' }, // red
  { light: '#59359a', dark: '#a98eda' }, // purple
  { light: '#ca6510', dark: '#feb272' }, // orange
  { light: '#087990', dark: '#6edff6' }, // cyan
  { light: '#ab296a', dark: '#e685b5' }, // pink
  { light: '#997404', dark: '#ffda6a' }, // yellow
];

/** The surfaces the palette is checked against: Bootstrap 5.3's `--bs-body-bg` per theme. */
export const LIGHT_BACKGROUND = '#ffffff';
export const DARK_BACKGROUND = '#212529';

/** WCAG relative luminance of a `#rrggbb` or `rgb(r, g, b)` colour; null when unparsable. */
export function relativeLuminance(colour: string): number | null {
  const rgb = parseColour(colour);
  if (!rgb) return null;
  const [r, g, b] = rgb.map((c) => {
    const s = c / 255;
    return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

export function contrastRatio(a: string, b: string): number {
  const la = relativeLuminance(a);
  const lb = relativeLuminance(b);
  if (la === null || lb === null) return 0;
  const [hi, lo] = la > lb ? [la, lb] : [lb, la];
  return (hi + 0.05) / (lo + 0.05);
}

/** Whether a background colour is dark: the theme is read from what is painted, so "auto" needs no special case. */
export function isDarkBackground(colour: string): boolean {
  const l = relativeLuminance(colour);
  return l !== null && l < 0.18;
}

function parseColour(colour: string): [number, number, number] | null {
  const c = colour.trim();
  let m = /^#([0-9a-f]{6})$/i.exec(c);
  if (m) {
    const n = parseInt(m[1], 16);
    return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
  }
  m = /^#([0-9a-f]{3})$/i.exec(c);
  if (m) {
    const [r, g, b] = m[1].split('').map((h) => parseInt(h + h, 16));
    return [r, g, b];
  }
  m = /^rgba?\(\s*(\d+)[,\s]+(\d+)[,\s]+(\d+)/i.exec(c);
  if (m) return [+m[1], +m[2], +m[3]];
  return null;
}
