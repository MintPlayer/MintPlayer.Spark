const UNITS: [Intl.RelativeTimeFormatUnit, number][] = [
  ['year', 365 * 24 * 60 * 60],
  ['month', 30 * 24 * 60 * 60],
  ['week', 7 * 24 * 60 * 60],
  ['day', 24 * 60 * 60],
  ['hour', 60 * 60],
  ['minute', 60],
];

/**
 * `date` relative to `now` in `language` ("5 minutes ago", "il y a 5 minutes", "5 minuten geleden"),
 * through `Intl.RelativeTimeFormat`; "now" under a minute. Empty for a missing date.
 */
export function sparkRelativeTime(date: Date | null | undefined, now: number, language: string): string {
  if (!date) return '';
  const seconds = Math.round((date.getTime() - now) / 1000);
  let format: Intl.RelativeTimeFormat;
  try {
    format = new Intl.RelativeTimeFormat(language || 'en', { numeric: 'auto' });
  } catch {
    format = new Intl.RelativeTimeFormat('en', { numeric: 'auto' });
  }
  for (const [unit, size] of UNITS) {
    if (Math.abs(seconds) >= size) return format.format(Math.trunc(seconds / size), unit);
  }
  return format.format(0, 'second');
}
