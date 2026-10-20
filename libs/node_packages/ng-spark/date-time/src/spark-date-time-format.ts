import { currentLanguage } from '@mintplayer/ng-spark/models';

/**
 * The ONE place in the client where an instant becomes local text or a time zone is named (K10). Everything
 * else (grids, detail pages, custom pages, the identity provider, the apps) renders through
 * `<spark-date-time>`, the `sparkDateTime` pipe or {@link formatSparkDateTime}; `tools/verify-time-zones.test.mjs`
 * fails the build on an `Intl.DateTimeFormat`, a `timeZone:` option or a `DatePipe` zone argument anywhere else.
 *
 * Built on `Intl.DateTimeFormat` rather than Angular's `DatePipe`: `DatePipe` ignores an IANA zone id and
 * silently falls back to the process's zone (docs/guide-dates-and-sorting.md, "The cookie reaches .NET"), and
 * needs locale data registered per language.
 */

/** Which wall clock a rendering shows: the viewer's zone, the stored wall clock with its offset, or a fixed IANA zone. */
export type SparkDateTimeMode = 'viewer' | 'stored' | { zone: string };

/**
 * How much is written. `short` is what the grids always showed (Angular's `'short'`): short date, short time.
 * `medium` and `long` widen the date; `date` and `time` drop the other half.
 */
export type SparkDateTimeStyle = 'short' | 'medium' | 'long' | 'date' | 'time';

const STYLES: Record<SparkDateTimeStyle, Intl.DateTimeFormatOptions> = {
  short: { dateStyle: 'short', timeStyle: 'short' },
  medium: { dateStyle: 'medium', timeStyle: 'short' },
  long: { dateStyle: 'full', timeStyle: 'short' },
  date: { dateStyle: 'medium' },
  time: { timeStyle: 'short' },
};

/** One rendering: its text, and a key that is equal for two renderings exactly when their wall clocks are. */
export interface SparkDateTimeRendering {
  text: string;
  wallClock: string;
}

/**
 * Parses a wire value into a `Date`, or `null` when it is absent or unparseable. The contract of
 * `parsedDate` and `queryCellValue`: `new Date(...)`, which keeps the instant and forgets the offset.
 */
export function parseSparkInstant(value: unknown): Date | null {
  if (value == null || value === '') return null;
  if (value instanceof Date) return Number.isNaN(value.getTime()) ? null : value;
  if (typeof value !== 'string' && typeof value !== 'number') return null;
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? null : parsed;
}

/**
 * The offset, in minutes east of UTC, that an ISO-8601 wire value was written with (`…+02:00` → 120,
 * `…Z` → 0); `null` when it carries none (a `Date`, a date-only string, a local time).
 */
export function wireOffsetMinutes(value: unknown): number | null {
  if (typeof value !== 'string') return null;
  const match = /T\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?(Z|[+-]\d{2}:?\d{2})$/i.exec(value.trim());
  if (!match) return null;
  const offset = match[1];
  if (offset.toUpperCase() === 'Z') return 0;
  const digits = offset.replace(':', '');
  const minutes = Number(digits.slice(1, 3)) * 60 + Number(digits.slice(3, 5));
  return offset.startsWith('-') ? -minutes : minutes;
}

/** `UTC`, `UTC+02:00`, `UTC-03:30`. Must match `SparkDateTimeFormatter.OffsetLabel` on the server. */
export function sparkOffsetLabel(minutes: number): string {
  if (minutes === 0) return 'UTC';
  const abs = Math.abs(minutes);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `UTC${minutes < 0 ? '-' : '+'}${pad(Math.floor(abs / 60))}:${pad(abs % 60)}`;
}

const formatters = new Map<string, Intl.DateTimeFormat | null>();

/** A cached formatter; `null` when the zone (or, after a retry without it, anything) is refused. */
function formatter(language: string | undefined, options: Intl.DateTimeFormatOptions, key: string): Intl.DateTimeFormat | null {
  const cacheKey = `${language ?? ''}|${key}`;
  if (formatters.has(cacheKey)) return formatters.get(cacheKey) ?? null;

  let created: Intl.DateTimeFormat | null = null;
  try {
    created = new Intl.DateTimeFormat(language || undefined, options);
  } catch {
    // An unknown language tag falls back to the browser's own; an unknown zone has no fallback.
    try {
      created = language ? new Intl.DateTimeFormat(undefined, options) : null;
    } catch {
      created = null;
    }
  }
  formatters.set(cacheKey, created);
  return created;
}

/** The wall clock of `date` in `timeZone` (undefined = the viewer's), as a locale-independent comparable key. */
function wallClockKey(date: Date, timeZone: string | undefined): string | null {
  const f = formatter('en-US', {
    timeZone, hourCycle: 'h23', year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit',
  }, `key|${timeZone ?? ''}`);
  return f ? f.format(date) : null;
}

/**
 * Renders a wire value in one zone. `null` when the value is not a date, or names a zone this browser does
 * not know. A `'stored'` rendering of a value without an offset (a `Date`, a local string) is the viewer's.
 */
export function renderSparkDateTime(
  value: unknown,
  mode: SparkDateTimeMode = 'viewer',
  style: SparkDateTimeStyle = 'short',
  language: string = currentLanguage(),
): SparkDateTimeRendering | null {
  const date = parseSparkInstant(value);
  if (!date) return null;

  if (mode === 'stored') {
    const offset = wireOffsetMinutes(value);
    if (offset !== null) {
      // The stored wall clock is the instant shifted by its own offset, read in UTC.
      const shifted = new Date(date.getTime() + offset * 60_000);
      const f = formatter(language, { ...STYLES[style], timeZone: 'UTC' }, `${style}|UTC`);
      const key = wallClockKey(shifted, 'UTC');
      return f && key ? { text: `${f.format(shifted)} ${sparkOffsetLabel(offset)}`, wallClock: key } : null;
    }
    mode = 'viewer';
  }

  const timeZone = mode === 'viewer' ? undefined : mode.zone;
  const f = formatter(language, { ...STYLES[style], timeZone }, `${style}|${timeZone ?? ''}`);
  const key = wallClockKey(date, timeZone);
  return f && key ? { text: f.format(date), wallClock: key } : null;
}

/**
 * The text of a wire value in one zone: `''` for an absent value, the value itself when it is not a date
 * (a stored value is never hidden), and the viewer's zone when a named zone is unknown here.
 */
export function formatSparkDateTime(
  value: unknown,
  mode: SparkDateTimeMode = 'viewer',
  style: SparkDateTimeStyle = 'short',
  language: string = currentLanguage(),
): string {
  if (value == null || value === '') return '';
  const rendering = renderSparkDateTime(value, mode, style, language)
    ?? (mode === 'viewer' ? null : renderSparkDateTime(value, 'viewer', style, language));
  return rendering?.text ?? String(value);
}

/**
 * The second rendering (D35): `value` in `alsoIn` (an IANA id, or `'stored'`), or `null` when it is hidden
 * because both renderings give the same wall clock at that instant. Compared by wall clock on the value's
 * own date, never by zone name: Europe/Paris against a viewer in Europe/Brussels shows one time.
 */
export function sparkDateTimeHint(
  value: unknown,
  primary: SparkDateTimeMode,
  alsoIn: string | null | undefined,
  style: SparkDateTimeStyle = 'short',
  language: string = currentLanguage(),
): SparkDateTimeRendering | null {
  if (!alsoIn) return null;
  const main = renderSparkDateTime(value, primary, style, language);
  const hint = renderSparkDateTime(value, alsoIn === 'stored' ? 'stored' : { zone: alsoIn }, style, language);
  if (!main || !hint || main.wallClock === hint.wallClock) return null;
  return hint;
}

/** The viewer's IANA zone as the browser reports it, or `null`. */
export function sparkViewerTimeZone(): string | null {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || null;
  } catch {
    return null;
  }
}

/** Every IANA zone this browser knows (`Intl.supportedValuesOf`), or `[]` where it is unsupported. */
export function sparkTimeZoneIds(): string[] {
  try {
    return typeof Intl.supportedValuesOf === 'function' ? Intl.supportedValuesOf('timeZone') : [];
  } catch {
    return [];
  }
}

/**
 * The English city an IANA id names, the last step of the label fallback (D36): `Europe/Brussels` →
 * `Brussels`, `America/Argentina/Buenos_Aires` → `Buenos Aires`. Must match `SparkTimeZones.CityFromId`.
 */
export function sparkTimeZoneCity(zone: string): string {
  const id = (zone ?? '').trim();
  if (!id) return 'UTC';
  const city = id.slice(id.lastIndexOf('/') + 1);
  return city ? city.replace(/_/g, ' ') : id;
}

/**
 * Step 2 of the label fallback (D36, S23): the zone's generic name as the platform localizes it
 * (`Intl` `longGeneric`: "Midden-Europese tijd"), or `null` when the browser has none.
 */
export function sparkGenericZoneName(zone: string, language: string): string | null {
  try {
    const parts = new Intl.DateTimeFormat(language || undefined, { timeZone: zone, timeZoneName: 'longGeneric' }).formatToParts(new Date());
    return parts.find(p => p.type === 'timeZoneName')?.value || null;
  } catch {
    return null;
  }
}
