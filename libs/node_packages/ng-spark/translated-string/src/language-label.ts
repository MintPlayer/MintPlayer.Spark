import { resolveTranslation, type TranslatedString, type TranslatedStringEntry } from '@mintplayer/ng-spark/models';

/**
 * The name of the language `tag` for a reader of `uiLanguage`: the app's own name for it
 * (`culture.languages.{code}` in translations.json) when the app offers it, else the browser's
 * `Intl.DisplayNames` (K11: a standard API, no table of our own), else the tag itself.
 */
export function languageLabel(tag: string, uiLanguage: string, appLanguages: Record<string, TranslatedString>): string {
  const own = Object.entries(appLanguages).find(([code]) => code.toLowerCase() === tag.toLowerCase())?.[1];
  const ownText = own ? resolveTranslation(own, uiLanguage) : '';
  if (ownText && ownText.toLowerCase() !== tag.toLowerCase()) return ownText;
  try {
    return new Intl.DisplayNames([uiLanguage], { type: 'language' }).of(tag) ?? tag;
  } catch {
    // An ill-formed tag or UI language: RangeError.
    return tag;
  }
}

/**
 * The RFC 5646 canonical form of a tag the user typed (`zh-hant` → `zh-Hant`), or null when it is not a
 * well-formed tag. `Intl.getCanonicalLocales` is the standard check (K11); the server canonicalizes again.
 */
export function canonicalLanguageTag(tag: string): string | null {
  const trimmed = tag.trim();
  if (!trimmed) return null;
  try {
    return Intl.getCanonicalLocales(trimmed)[0] ?? null;
  } catch {
    return null;
  }
}

/** An app text: no kind and no validity window (mirrors `TranslatedStringEntry.IsPlain`, C#). */
export function isPlainEntry(entry: TranslatedStringEntry): boolean {
  return !entry.Kind && !entry.ValidFrom && !entry.ValidTo;
}
