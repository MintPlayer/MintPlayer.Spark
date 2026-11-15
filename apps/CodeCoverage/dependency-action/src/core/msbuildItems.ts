import { compareChannels, tfmChannel } from '../platform/channels';
import { blockFrom, type TextDoc } from './lineEdit';

/**
 * multi-target-dependency-split-PRD D-a/S3: the package items of an MSBuild file with the conditions over them. A line
 * scan, like every other MSBuild edit of the action (S13: the dotnet CLI reformats the file): one `ItemGroup` tag per
 * line, and an item either self-closing on its line or closed by its own end tag.
 */

export type ItemElement = 'PackageReference' | 'PackageVersion' | 'GlobalPackageReference';

export interface ItemGroupSpan {
  /** The `<ItemGroup>` line. */
  start: number;
  /** One past the `</ItemGroup>` line. */
  end: number;
  condition: string | null;
}

export interface MsBuildItem {
  element: ItemElement;
  include: string;
  /** The `Version` attribute or `<Version>` child; null when the item names no version (CPM, `$(Prop)` excepted). */
  version: string | null;
  /** The item's lines, [start, end). */
  start: number;
  end: number;
  ownCondition: string | null;
  group: ItemGroupSpan | null;
}

const ITEM = /<(PackageReference|PackageVersion|GlobalPackageReference)\b[^>]*?\b(?:Include|Update)\s*=\s*"([^"]+)"/;
const GROUP_OPEN = /<ItemGroup\b([^>]*?)(\/?)>/;
const GROUP_CLOSE = /<\/ItemGroup\s*>/;

/** The `Condition` attribute of a tag's text, either quoting. */
export function conditionAttribute(text: string): string | null {
  const m = text.match(/\bCondition\s*=\s*(?:"([^"]*)"|'([^']*)')/);
  if (!m) return null;
  return (m[1] ?? m[2] ?? '').trim();
}

export function scanItems(doc: TextDoc): MsBuildItem[] {
  const out: MsBuildItem[] = [];
  let group: ItemGroupSpan | null = null;
  const groups: ItemGroupSpan[] = [];
  for (let i = 0; i < doc.lines.length; i++) {
    const text = doc.lines[i].text;
    const open = text.match(GROUP_OPEN);
    if (open && open[2] !== '/') {
      group = { start: i, end: doc.lines.length, condition: conditionAttribute(open[1]) };
      groups.push(group);
    }
    const item = text.match(ITEM);
    if (item) {
      const element = item[1] as ItemElement;
      const selfClosing = /\/>\s*$/.test(text.slice(item.index!)) || new RegExp(`</${element}>`).test(text);
      const [start, end] = selfClosing ? [i, i + 1] : blockFrom(doc, i, new RegExp(`/>|</${element}>`));
      const lines = doc.lines.slice(start, end).map((l) => l.text);
      const version = lines[0].match(/\bVersion\s*=\s*"([^"]*)"/)?.[1] ?? lines.join('\n').match(/<Version>\s*([^<]*?)\s*<\/Version>/)?.[1] ?? null;
      out.push({ element, include: item[2], version, start, end, ownCondition: conditionAttribute(lines[0].slice(item.index!)), group });
      i = end - 1;
      if (GROUP_CLOSE.test(doc.lines[i].text) && group) {
        group.end = i + 1;
        group = null;
      }
      continue;
    }
    if (GROUP_CLOSE.test(text) && group) {
      group.end = i + 1;
      group = null;
    }
  }
  return out;
}

/** The condition over an item: its ItemGroup's and its own; both at once are joined, which no rule maps (D-f). */
export function conditionOf(item: MsBuildItem): string | null {
  const parts = [item.group?.condition, item.ownCondition].filter((c): c is string => !!c && c.length > 0);
  if (parts.length === 0) return null;
  return parts.length === 1 ? parts[0] : parts.map((p) => `(${p})`).join(' And ');
}

const TFM = String.raw`'?\$\(TargetFramework\)'?`;
const LITERAL = String.raw`'([A-Za-z0-9.+-]+)'`;
const EQUALITY = new RegExp(String.raw`^${TFM}\s*(==|!=)\s*${LITERAL}$|^${LITERAL}\s*(==|!=)\s*${TFM}$`, 'i');
const COMPATIBLE = new RegExp(String.raw`^\$\(\[MSBuild\]::IsTargetFrameworkCompatible\(\s*'\$\(TargetFramework\)'\s*,\s*${LITERAL}\s*\)\)$`, 'i');

/** The canonical condition the bot writes (S3): one TFM, `==`. */
export function canonicalCondition(tfm: string): string {
  return `'$(TargetFramework)' == '${tfm}'`;
}

/**
 * S3: the project TFMs a condition selects, or null when it maps to none the scan can name: `== 'X'`, `!= 'X'` (either
 * side) and `IsTargetFrameworkCompatible('$(TargetFramework)', 'X')` (the project's modern TFMs at or above X's channel).
 */
export function conditionTfms(condition: string, projectTfms: readonly string[]): string[] | null {
  const text = condition.trim();
  const eq = text.match(EQUALITY);
  if (eq) {
    const op = eq[1] ?? eq[4];
    const value = (eq[2] ?? eq[3]).toLowerCase();
    if (op === '==') return [projectTfms.find((t) => t.toLowerCase() === value) ?? value];
    const rest = projectTfms.filter((t) => t.toLowerCase() !== value);
    return rest.length > 0 ? rest : null;
  }
  const compat = text.match(COMPATIBLE);
  if (compat) {
    const floor = tfmChannel(compat[1]);
    if (floor === null) return null;
    const hits = projectTfms.filter((t) => {
      const c = tfmChannel(t);
      return c !== null && compareChannels(c, floor) >= 0;
    });
    return hits.length > 0 ? hits : null;
  }
  return null;
}

/** A project's own unconditioned, literal `<TargetFramework(s)>` list, or null when it has none. */
export function ownTfms(text: string): string[] | null {
  for (const m of text.matchAll(/<(TargetFrameworks?)(\s[^>]*)?>([^<]*)<\/\1>/g)) {
    if (m[2] && /\bCondition\s*=/.test(m[2])) continue;
    if (/\$[({]/.test(m[3])) return null;
    const tokens = m[3].split(';').map((t) => t.trim()).filter(Boolean);
    if (tokens.length > 0) return tokens;
  }
  return null;
}

/** The distinct channels of a TFM list (`net10.0;net11.0-windows` → 10.0, 11.0). */
export function channelsOf(tfms: readonly string[]): string[] {
  return [...new Set(tfms.map((t) => tfmChannel(t)).filter((c): c is string => c !== null))];
}
