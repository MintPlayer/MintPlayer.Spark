import { signal, type WritableSignal } from '@angular/core';

export type TranslatedString = Record<string, string>;

/** Global reactive language state — shared across library boundaries via globalThis */
export const currentLanguage: WritableSignal<string> =
  ((globalThis as any).__sparkCurrentLanguage ??= signal('en'));

/**
 * The language to render in: {@link currentLanguage} in the browser; during a server render, the
 * language of the request being rendered.
 *
 * One Node process renders many requests at once, and they interleave at every `await`, so a
 * process-wide signal cannot hold a per-request value there. `@mintplayer/ng-spark/ssr` installs
 * `__sparkScopedLanguage`, which reads the render's language from an AsyncLocalStorage scope; outside a
 * render it returns undefined. Read the language through this function, never `currentLanguage()`
 * directly, in anything that can run on the server.
 */
export function activeLanguage(): string {
  const scoped = (globalThis as { __sparkScopedLanguage?: () => string | undefined }).__sparkScopedLanguage;
  return scoped?.() ?? currentLanguage();
}

export function resolveTranslation(ts: TranslatedString | undefined, lang?: string): string {
  if (!ts) return '';
  const language = lang ?? activeLanguage();
  return ts[language] ?? ts['en'] ?? Object.values(ts)[0] ?? '';
}
