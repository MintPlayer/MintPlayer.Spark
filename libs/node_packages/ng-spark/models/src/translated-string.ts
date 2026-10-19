import { signal, type WritableSignal } from '@angular/core';

/**
 * Spark's one name system (commerce X17): app texts, labels, messages and names of places, in any
 * language. The same shape the server stores and sends — member names are the C# names on purpose
 * (RavenDB indexes address the stored JSON by them):
 *
 * ```json
 * { "Entries": [{ "Tag": "en", "Value": "Save" }, { "Tag": "nl", "Value": "Opslaan", "Kind": "official" }] }
 * ```
 *
 * Build one in code with {@link translated}: `translated({ en: 'Save', nl: 'Opslaan' })`.
 */
export interface TranslatedString {
  Entries: TranslatedStringEntry[];
}

/** One text: an RFC 5646 tag and a value; a place name may add the rest. */
export interface TranslatedStringEntry {
  /** RFC 5646 language tag in canonical case: `nl`, `zh-Hant`, `sr-Latn`. */
  Tag: string;
  Value: string;
  Kind?: TranslatedStringKind;
  /** First day the name applies (inclusive), `yyyy-MM-dd`. */
  ValidFrom?: string;
  /** First day the name no longer applies (exclusive), `yyyy-MM-dd`. */
  ValidTo?: string;
  /** Where the name comes from (a register, GNS, Wikidata, …). */
  Source?: string;
  /** Set on a romanization: published by a source, or generated. */
  Romanization?: RomanizationOrigin;
}

export type TranslatedStringKind = 'official' | 'common' | 'exonym' | 'historic';
export type RomanizationOrigin = 'authoritative' | 'generated';

/** The language every Spark text is authored in; the last language of the fallback chain. */
export const DEFAULT_LANGUAGE = 'en';

/** Global reactive language state — shared across library boundaries via globalThis */
export const currentLanguage: WritableSignal<string> =
  ((globalThis as any).__sparkCurrentLanguage ??= signal('en'));

/** An app text, one plain entry per language: `translated({ en: 'Copy', fr: 'Copier', nl: 'Kopiëren' })`. */
export function translated(texts: Record<string, string>): TranslatedString {
  return { Entries: Object.entries(texts).map(([Tag, Value]) => ({ Tag, Value })) };
}

/** Whether `value` has the {@link TranslatedString} shape (an attribute value arrives as `unknown`). */
export function isTranslatedString(value: unknown): value is TranslatedString {
  return !!value && typeof value === 'object' && Array.isArray((value as TranslatedString).Entries);
}

/**
 * The RFC 4647 §3.4 lookup chain of one language range: the range, then shorter by dropping the last
 * subtag, also dropping a singleton left at the end (`zh-Hant-TW-x-a` → `zh-Hant-TW` → `zh-Hant` → `zh`).
 * The wildcard `*` and blanks yield nothing. Mirrors `LanguageTag.LookupChain` (C#).
 */
export function lookupChain(range: string | null | undefined): string[] {
  const chain: string[] = [];
  let current = (range ?? '').trim();
  if (!current || current === '*') return chain;
  while (current.length > 0) {
    chain.push(current);
    const dash = current.lastIndexOf('-');
    if (dash < 0) break;
    current = current.substring(0, dash);
    if (current.length >= 2 && current[current.length - 2] === '-') current = current.substring(0, current.length - 2);
  }
  return chain;
}

/** Not historic, and `day` (`yyyy-MM-dd`) in `[ValidFrom, ValidTo)`. */
export function isCurrentEntry(entry: TranslatedStringEntry, day: string): boolean {
  return entry.Kind !== 'historic'
    && (!entry.ValidFrom || entry.ValidFrom <= day)
    && (!entry.ValidTo || day < entry.ValidTo);
}

export interface ResolveTranslationOptions {
  /** Default {@link DEFAULT_LANGUAGE}; `null` for none. */
  defaultLanguage?: string | null;
  /** `yyyy-MM-dd`; default today (UTC). */
  asOf?: string;
}

/**
 * The text of `ts` for the requested language(s) — the fallback chain `TranslatedString.Resolve` (C#)
 * uses, kept identical by the shared fixture `translated-string.fixture.json`:
 * 1. for each requested range in order, then the default language: its RFC 4647 lookup chain, and for
 *    each tag in it the first *current* entry with that tag (tags compare case-insensitively);
 * 2. otherwise the first current entry;
 * 3. otherwise the first entry; otherwise `''`.
 */
export function resolveTranslation(
  ts: TranslatedString | null | undefined,
  lang?: string | readonly string[],
  options?: ResolveTranslationOptions,
): string {
  const entries = ts?.Entries;
  if (!entries?.length) return '';
  const day = options?.asOf ?? new Date().toISOString().substring(0, 10);
  const requested = lang === undefined ? [currentLanguage()] : typeof lang === 'string' ? [lang] : [...lang];
  const defaultLanguage = options?.defaultLanguage === undefined ? DEFAULT_LANGUAGE : options.defaultLanguage;

  for (const range of [...requested, defaultLanguage]) {
    for (const candidate of lookupChain(range)) {
      const wanted = candidate.toLowerCase();
      const match = entries.find(e => isCurrentEntry(e, day) && e.Tag.toLowerCase() === wanted);
      if (match) return match.Value;
    }
  }

  return entries.find(e => isCurrentEntry(e, day))?.Value ?? entries[0].Value;
}
