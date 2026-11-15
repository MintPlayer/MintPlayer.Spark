import type { PlanItem } from '../../contracts/types';
import { EditRefused, type Line, type TextDoc } from '../../core/lineEdit';
import { canonicalCondition, conditionAttribute, scanItems, type MsBuildItem } from '../../core/msbuildItems';

/**
 * multi-target-dependency-split-PRD D-c/D-d/D-e: one package of one MSBuild file rewritten into one canonical
 * `<ItemGroup Condition="'$(TargetFramework)' == 'X'">` per layout entry. Split, added TFM, dropped TFM and per-TFM patches
 * are all this one edit. The item itself (attributes, children, formatting) is copied from the file with only its version
 * changed, so the writer's "same XML, regrouped" check (S4) holds by construction.
 *
 * Refused (never guessed): an occurrence under any other condition than the canonical one (the bot plans none, D-f), an
 * item with a Condition of its own, a version through `$(Prop)`, and an item sharing its line with an ItemGroup tag.
 */
export function applyLayout(doc: TextDoc, file: string, item: PlanItem): void {
  const layout = item.layout ?? [];
  if (layout.length === 0) throw new EditRefused(file, `${item.depName}: empty layout`, 0);
  const all = scanItems(doc);
  const mine = all.filter((i) => i.include.toLowerCase() === item.depName.toLowerCase() && i.element !== 'GlobalPackageReference');
  if (mine.length === 0) throw new EditRefused(file, `no ${item.depName} reference`, 0);
  for (const occ of mine) refuseUnless(file, item.depName, doc, occ);

  const eol = doc.lines.find((l) => l.eol !== '')?.eol ?? '\n';
  const template = mine.find((o) => o.group?.condition == null) ?? mine[0];
  const itemLines = doc.lines.slice(template.start, template.end).map((l) => l.text);
  const groupIndent = template.group ? indentOf(doc.lines[template.group.start].text) : indentOf(itemLines[0]).slice(0, -2);

  const deleted = new Set<number>();
  const before = new Map<number, Line[]>();
  const after = new Map<number, Line[]>();
  const at = (map: Map<number, Line[]>, line: number, lines: Line[]) => map.set(line, [...(map.get(line) ?? []), ...lines]);
  for (const occ of mine) for (let i = occ.start; i < occ.end; i++) deleted.add(i);

  const groups = canonicalGroups(doc);
  const filled = new Set<number>();
  const fresh: Line[] = [];
  for (const entry of layout) {
    const lines = withVersion(itemLines, entry.version, file, item.depName).map((text) => ({ text, eol }));
    const group = groups.get(entry.targetFramework.toLowerCase());
    if (group) {
      // Back where the package was in that group, else last in it.
      const own = mine.find((o) => o.group?.start === group.start);
      at(before, own ? own.start : group.end - 1, lines);
      filled.add(group.start);
      continue;
    }
    fresh.push({ text: `${groupIndent}<ItemGroup Condition="${canonicalCondition(entry.targetFramework)}">`, eol }, ...lines, { text: `${groupIndent}</ItemGroup>`, eol });
  }

  // An ItemGroup whose every item went, and into which nothing comes back, goes too.
  const emptied: Array<{ start: number; end: number }> = [];
  for (const occ of mine) {
    const g = occ.group;
    if (!g || filled.has(g.start) || emptied.some((e) => e.start === g.start)) continue;
    let empty = true;
    for (let i = g.start + 1; i < g.end - 1; i++) if (!deleted.has(i) && doc.lines[i].text.trim() !== '') empty = false;
    if (empty) emptied.push({ start: g.start, end: g.end });
  }
  for (const g of emptied) for (let i = g.start; i < g.end; i++) deleted.add(i);

  if (fresh.length > 0) {
    // After the package's last ItemGroup (gone or not), so the groups stay in TFM order.
    const last = Math.max(...mine.map((o) => (o.group ? o.group.end - 1 : -1)));
    at(after, last >= 0 ? last : lastProjectLine(doc) - 1, fresh);
  }

  const out: Line[] = [];
  doc.lines.forEach((line, i) => {
    out.push(...(before.get(i) ?? []));
    if (!deleted.has(i)) out.push(line);
    out.push(...(after.get(i) ?? []));
  });
  // A group that went leaves no double blank line behind.
  for (let i = out.length - 1; i > 0; i--) if (out[i].text.trim() === '' && out[i - 1].text.trim() === '' && out[i].eol !== '') out.splice(i, 1);
  doc.lines = out;
}

function refuseUnless(file: string, dep: string, doc: TextDoc, occ: MsBuildItem): void {
  if (occ.ownCondition) throw new EditRefused(file, `${dep} has a Condition of its own: moved by hand`, 1);
  const g = occ.group?.condition;
  if (g != null && !/^'\$\(TargetFramework\)' == '[A-Za-z0-9.+-]+'$/.test(g)) throw new EditRefused(file, `${dep} is under a condition the bot does not write (${g})`, 1);
  if (occ.version === null || occ.version.includes('$(')) throw new EditRefused(file, `${dep} names no literal version`, 1);
  for (let i = occ.start; i < occ.end; i++) if (/<\/?ItemGroup\b/.test(doc.lines[i].text)) throw new EditRefused(file, `${dep} shares a line with an ItemGroup tag`, 1);
}

function indentOf(text: string): string {
  return text.match(/^\s*/)![0];
}

function withVersion(lines: string[], version: string, file: string, dep: string): string[] {
  const out = [...lines];
  if (/\bVersion\s*=\s*"[^"]*"/.test(out[0])) {
    out[0] = out[0].replace(/(\bVersion\s*=\s*")[^"]*(")/, `$1${version}$2`);
    return out;
  }
  const k = out.findIndex((l) => /<Version>[^<]*<\/Version>/.test(l));
  if (k < 0) throw new EditRefused(file, `${dep}: no version to write`, 0);
  out[k] = out[k].replace(/(<Version>)[^<]*(<\/Version>)/, `$1${version}$2`);
  return out;
}

/** The file's canonical ItemGroups by TFM (lower-case): `[start, end)`. */
function canonicalGroups(doc: TextDoc): Map<string, { start: number; end: number }> {
  const out = new Map<string, { start: number; end: number }>();
  for (let i = 0; i < doc.lines.length; i++) {
    const open = doc.lines[i].text.match(/<ItemGroup\b([^>]*?)>/);
    if (!open || open[1].endsWith('/')) continue;
    const condition = conditionAttribute(open[1]);
    const tfm = condition?.match(/^'\$\(TargetFramework\)' == '([A-Za-z0-9.+-]+)'$/)?.[1];
    let end = i + 1;
    while (end < doc.lines.length && !/<\/ItemGroup\s*>/.test(doc.lines[end - 1].text)) end++;
    if (tfm && !out.has(tfm.toLowerCase())) out.set(tfm.toLowerCase(), { start: i, end });
  }
  return out;
}

function lastProjectLine(doc: TextDoc): number {
  for (let i = doc.lines.length - 1; i >= 0; i--) if (/<\/Project\s*>/.test(doc.lines[i].text)) return i;
  return doc.lines.length;
}
