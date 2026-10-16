/**
 * The shared text-edit core (S13: "unique-match line edit, EOL and BOM byte-preserving, refuse on 0
 * or more than 1 match").
 *
 * Every manifest edit in this action goes through here, which is what keeps the patch *pure* (gap A:
 * each -/+ pair differs only by the planned current -> new token on a line naming the dependency).
 * Native "add/update" commands were measured to re-sort, reflow or rewrite whole files (S13), so no
 * adapter lets a package manager write a manifest.
 */

const BOM = Buffer.from([0xef, 0xbb, 0xbf]);

export interface Line {
  text: string;
  /** '\r\n', '\n', '\r' or '' (last line without a newline). */
  eol: string;
}

export interface TextDoc {
  bom: boolean;
  lines: Line[];
}

export class EditRefused extends Error {
  constructor(
    readonly file: string,
    readonly reason: string,
    readonly matches: number,
  ) {
    super(`${file}: ${reason} (${matches} match${matches === 1 ? '' : 'es'})`);
    this.name = 'EditRefused';
  }
}

export function parseText(bytes: Buffer, file = '<buffer>'): TextDoc {
  const bom = bytes.length >= 3 && bytes.subarray(0, 3).equals(BOM);
  const body = bom ? bytes.subarray(3) : bytes;
  const text = body.toString('utf8');
  // Refuse anything that does not survive a UTF-8 round trip: editing it could not be byte-exact.
  if (!Buffer.from(text, 'utf8').equals(body)) {
    throw new EditRefused(file, 'file is not valid UTF-8', 0);
  }
  const lines: Line[] = [];
  const re = /\r\n|\n|\r/g;
  let start = 0;
  let m: RegExpExecArray | null;
  while ((m = re.exec(text)) !== null) {
    lines.push({ text: text.slice(start, m.index), eol: m[0] });
    start = m.index + m[0].length;
  }
  if (start < text.length || lines.length === 0) {
    lines.push({ text: text.slice(start), eol: '' });
  }
  return { bom, lines };
}

export function serializeText(doc: TextDoc): Buffer {
  const body = Buffer.from(doc.lines.map((l) => l.text + l.eol).join(''), 'utf8');
  return doc.bom ? Buffer.concat([BOM, body]) : body;
}

export function escapeRegExp(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\/]/g, '\\$&');
}

export interface TokenEdit {
  /**
   * Must contain a named group `v` around the token to replace. Matched per line; flags are forced to
   * `dg` (indices + global) so every occurrence on a line is counted.
   */
  find: RegExp;
  /** The replacement for group `v`. */
  to: string;
  /** Restrict the search to lines [from, to). */
  range?: [number, number];
  /** Optional extra line filter (e.g. "the line also names the dependency"). */
  lineFilter?: (text: string) => boolean;
}

function withFlags(re: RegExp): RegExp {
  const flags = new Set(re.flags.split(''));
  flags.add('d');
  flags.add('g');
  return new RegExp(re.source, [...flags].join(''));
}

interface Hit {
  line: number;
  start: number;
  end: number;
}

export function findTokenHits(doc: TextDoc, edit: Pick<TokenEdit, 'find' | 'range' | 'lineFilter'>): Hit[] {
  const re = withFlags(edit.find);
  const [from, to] = edit.range ?? [0, doc.lines.length];
  const hits: Hit[] = [];
  for (let i = from; i < Math.min(to, doc.lines.length); i++) {
    const text = doc.lines[i].text;
    if (edit.lineFilter && !edit.lineFilter(text)) continue;
    re.lastIndex = 0;
    let m: RegExpExecArray | null;
    while ((m = re.exec(text)) !== null) {
      const idx = m.indices?.groups?.v;
      if (!idx) throw new Error(`pattern ${edit.find} has no named group "v"`);
      hits.push({ line: i, start: idx[0], end: idx[1] });
      if (m[0].length === 0) re.lastIndex++;
    }
  }
  return hits;
}

/** Replaces exactly one token; refuses (throws EditRefused) on 0 or >1 matches. Returns the line index. */
export function replaceUniqueToken(doc: TextDoc, edit: TokenEdit, file = '<buffer>'): number {
  const hits = findTokenHits(doc, edit);
  if (hits.length !== 1) {
    throw new EditRefused(file, hits.length === 0 ? `no line matches ${edit.find.source}` : `ambiguous: ${edit.find.source}`, hits.length);
  }
  const [hit] = hits;
  const line = doc.lines[hit.line];
  line.text = line.text.slice(0, hit.start) + edit.to + line.text.slice(hit.end);
  return hit.line;
}

/** Index of the single line matching `re` inside `range`; throws EditRefused otherwise. */
export function findUniqueLine(doc: TextDoc, re: RegExp, file = '<buffer>', range?: [number, number]): number {
  const [from, to] = range ?? [0, doc.lines.length];
  const found: number[] = [];
  for (let i = from; i < Math.min(to, doc.lines.length); i++) {
    if (re.test(doc.lines[i].text)) found.push(i);
  }
  if (found.length !== 1) {
    throw new EditRefused(file, found.length === 0 ? `no anchor line ${re.source}` : `ambiguous anchor ${re.source}`, found.length);
  }
  return found[0];
}

/** Every line index matching `re`. */
export function findLines(doc: TextDoc, re: RegExp, range?: [number, number]): number[] {
  const [from, to] = range ?? [0, doc.lines.length];
  const out: number[] = [];
  for (let i = from; i < Math.min(to, doc.lines.length); i++) {
    if (re.test(doc.lines[i].text)) out.push(i);
  }
  return out;
}

/**
 * The block starting at `anchor` and ending at the first later line matching `end` (inclusive).
 * Returns [anchor, end + 1). If `end` never matches, the block runs `maxLines` lines.
 */
export function blockFrom(doc: TextDoc, anchor: number, end: RegExp, maxLines = 50): [number, number] {
  if (end.test(doc.lines[anchor].text)) return [anchor, anchor + 1];
  for (let i = anchor + 1; i < Math.min(doc.lines.length, anchor + maxLines); i++) {
    if (end.test(doc.lines[i].text)) return [anchor, i + 1];
  }
  return [anchor, Math.min(doc.lines.length, anchor + maxLines)];
}

/** Convenience: edit a buffer with one token edit and return the new bytes. */
export function editBytes(bytes: Buffer, edit: TokenEdit, file: string): Buffer {
  const doc = parseText(bytes, file);
  replaceUniqueToken(doc, edit, file);
  return serializeText(doc);
}
