/** One entry of a place name (the X17 `TranslatedString` entry shape as the API sends it). */
export interface PlaceNameEntry {
  tag: string;
  value: string;
  kind?: 'official' | 'common' | 'exonym' | 'historic' | string | null;
  validFrom?: string | null;
  validTo?: string | null;
  romanization?: 'authoritative' | 'generated' | string | null;
}

export interface PlaceDisplayName {
  local: string;
  ui: string | null;
  latin: string | null;
  display: string;
}

/** The authority's order joined with this (Q-R13): "Rue de la Loi - Wetstraat". */
export const PLACE_NAME_SEPARATOR = ' - ';

/** RFC 4647 lookup: `nl-BE` → `nl-BE`, `nl`. */
function lookupChain(range: string | null | undefined): string[] {
  if (!range) return [];
  const parts = range.split('-');
  const chain: string[] = [];
  for (let i = parts.length; i > 0; i--) {
    const tag = parts.slice(0, i).join('-');
    // A single-character subtag (x, u) never ends a lookup range.
    if (parts[i - 1].length === 1) continue;
    chain.push(tag);
  }
  return chain;
}

const same = (a: string | null | undefined, b: string | null | undefined) => (a ?? '').toLowerCase() === (b ?? '').toLowerCase();

function isCurrent(entry: PlaceNameEntry, asOf: string): boolean {
  return entry.kind !== 'historic'
    && (!entry.validFrom || entry.validFrom <= asOf)
    && (!entry.validTo || asOf < entry.validTo);
}

/**
 * The display rule (RD7, PRD §4.9): the TypeScript twin of the C# `PlaceNames.Display`, checked against the same cases.
 * 1. local: the official names valid at `asOf` in the official languages, in that order, joined with " - ";
 * 2. ui: if `ui` is an official language there, its official name alone;
 * 3. secondary: otherwise the exonym in `ui` after the local name, "Antwerpen (Anvers)";
 * 4. latin: for a non-Latin local name, the authoritative romanization (never a machine one, Q-R11).
 */
export function displayPlaceName(entries: readonly PlaceNameEntry[], officialLanguages: readonly string[], ui: string | null | undefined, asOf: string): PlaceDisplayName {
  const current = entries.filter(e => isCurrent(e, asOf));
  const isOfficial = (e: PlaceNameEntry) => !e.kind || e.kind === 'official';

  const local: string[] = [];
  for (const lang of officialLanguages) {
    const entry = current.find(e => isOfficial(e) && same(e.tag, lang));
    if (entry && !local.includes(entry.value)) local.push(entry.value);
  }
  if (local.length === 0) {
    const fallback = current.find(isOfficial) ?? current[0] ?? entries[0];
    if (fallback) local.push(fallback.value);
  }
  const localText = local.join(PLACE_NAME_SEPARATOR);

  let uiName: string | null = null;
  let display = localText;
  const chain = lookupChain(ui);
  if (chain.length > 0) {
    const officialHere = officialLanguages.some(l => chain.some(c => same(c, l)));
    if (officialHere) {
      uiName = chain.map(c => current.find(e => isOfficial(e) && same(e.tag, c))?.value).find(v => !!v) ?? null;
      display = uiName ?? localText;
    } else {
      const exonym = chain.map(c => current.find(e => e.kind === 'exonym' && same(e.tag, c))?.value).find(v => !!v) ?? null;
      if (exonym && !local.includes(exonym)) {
        uiName = exonym;
        display = `${localText} (${exonym})`;
      }
    }
  }

  const nonLatin = [...localText].some(c => /\p{L}/u.test(c) && c.codePointAt(0)! > 0x024f);
  const latin = nonLatin ? current.find(e => e.romanization === 'authoritative')?.value ?? null : null;
  return { local: localText, ui: uiName, latin, display };
}
