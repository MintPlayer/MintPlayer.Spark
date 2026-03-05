import { signal, type WritableSignal } from '@angular/core';

export type TranslatedString = Record<string, string>;

/** Global reactive language state — shared across library boundaries via globalThis */
export const currentLanguage: WritableSignal<string> =
  ((globalThis as any).__sparkCurrentLanguage ??= signal('en'));

export function resolveTranslation(ts: TranslatedString | undefined, lang?: string): string {
  if (!ts) return '';
  const language = lang ?? currentLanguage();
  return ts[language] ?? ts['en'] ?? Object.values(ts)[0] ?? '';
}
