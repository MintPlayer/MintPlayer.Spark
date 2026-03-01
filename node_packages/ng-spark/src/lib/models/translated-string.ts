export type TranslatedString = Record<string, string>;

export function resolveTranslation(ts: TranslatedString | undefined, lang?: string): string {
  if (!ts) return '';
  const language = lang ?? localStorage.getItem('spark-lang') ?? navigator.language?.split('-')[0] ?? 'en';
  return ts[language] ?? ts['en'] ?? Object.values(ts)[0] ?? '';
}
