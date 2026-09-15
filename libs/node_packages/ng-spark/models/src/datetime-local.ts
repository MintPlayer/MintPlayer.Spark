/**
 * Conversions between the wire format for a timestamp and what an `<input type="datetime-local">`
 * or `<input type="date">` will accept.
 *
 * A `DateTimeOffset` in Spark means an **instant**. The wire carries a full ISO-8601 string with an
 * offset (`2026-12-31T23:59:00-08:00`); an HTML date/time control speaks only bare local wall clock
 * (`2026-12-31T23:59`). Neither end can consume the other's format, so both directions need
 * converting, and the browser is deliberately the side that does it: its timezone rules are kept
 * fresh by the operating system, while a container's are frozen at image build time.
 *
 * Assigning a wire value straight to a `datetime-local` input does not throw and does not warn -- the
 * control simply renders **blank**, and saving the untouched form then writes that blank back.
 */

function pad(n: number): string {
  return String(n).padStart(2, '0');
}

/** Formats a UTC offset in minutes as `+HH:mm` / `-HH:mm`. */
function formatOffset(offsetMinutes: number): string {
  const sign = offsetMinutes >= 0 ? '+' : '-';
  const abs = Math.abs(offsetMinutes);
  return `${sign}${pad(Math.floor(abs / 60))}:${pad(abs % 60)}`;
}

/**
 * Parses a wire timestamp. Returns `null` for anything unusable, matching the contract of
 * `parsedDate` / `queryCellValue` -- a bad value renders as empty rather than as `Invalid Date`.
 */
export function parseWireDate(value: unknown): Date | null {
  if (value === null || value === undefined || value === '') return null;
  if (value instanceof Date) return isNaN(value.getTime()) ? null : value;
  if (typeof value !== 'string' && typeof value !== 'number') return null;

  const parsed = new Date(value);
  return isNaN(parsed.getTime()) ? null : parsed;
}

/**
 * Wire value -> the `yyyy-MM-ddTHH:mm` a `datetime-local` input needs, in the **viewer's** zone.
 *
 * The conversion is date-sensitive, not a fixed shift: a value is rendered using the zone's offset
 * *on that value's own date*, so a timestamp in July and one in January convert differently in a
 * zone that observes DST.
 */
export function toDateTimeLocalInput(value: unknown): string {
  const d = parseWireDate(value);
  if (!d) return '';
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/** Wire value -> the `yyyy-MM-dd` a `date` input needs, in the viewer's zone. */
export function toDateInput(value: unknown): string {
  const d = parseWireDate(value);
  if (!d) return '';
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

/**
 * `yyyy-MM-ddTHH:mm` from the control -> a complete ISO-8601 string carrying the viewer's offset
 * **for the entered date**.
 *
 * Building the `Date` from its parts rather than parsing the string is what makes this correct: it
 * lets the browser apply its own timezone rules for that date, including DST. Using the *current*
 * offset instead would be wrong for any value outside the present season -- entering a January date
 * during summer would record an offset an hour off, and the stored instant would be an hour wrong.
 *
 * Across a DST discontinuity the browser resolves the wall clock itself, taking the offset in force
 * before a fall-back and after a spring-forward. That is specified ECMAScript behaviour, and the
 * server's conversion helper deliberately reproduces the same choice so the two cannot drift.
 */
export function fromDateTimeLocalInput(local: unknown): string | null {
  if (typeof local !== 'string' || local === '') return null;

  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2}))?/.exec(local);
  if (!match) return null;

  const [, y, mo, d, h, mi, s] = match;
  const date = new Date(+y, +mo - 1, +d, +h, +mi, s ? +s : 0);
  if (isNaN(date.getTime())) return null;

  // getTimezoneOffset() is minutes to ADD to local to reach UTC, so it is the negation of the offset
  // an ISO string carries.
  return `${y}-${mo}-${d}T${h}:${mi}:${pad(s ? +s : 0)}${formatOffset(-date.getTimezoneOffset())}`;
}

/** `yyyy-MM-dd` from a `date` control -> an ISO string at local midnight with the viewer's offset. */
export function fromDateInput(local: unknown): string | null {
  if (typeof local !== 'string' || local === '') return null;
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(local);
  if (!match) return null;
  return fromDateTimeLocalInput(`${local}T00:00`);
}

/**
 * Compares two wire timestamps **by instant**, which is what a `DateTimeOffset` means in Spark.
 *
 * Round-tripping a value through a date control legitimately rewrites its text -- a document holding
 * `2026-12-31T23:59:00-08:00` comes back as `2027-01-01T08:59:00+01:00` from a viewer in Brussels --
 * so a string comparison would mark every untouched date as edited and send a needless write on every
 * save. Both of those name the same moment.
 *
 * Note this is the *opposite* of the trap on the server side, where `DateTimeOffset.Equals` compares
 * the instant and silently hides a destroyed offset. Here comparing by instant is the correct thing:
 * we are asking "did the user change this value", not "is the offset intact".
 */
export function wireDatesEqual(a: unknown, b: unknown): boolean {
  const da = parseWireDate(a);
  const db = parseWireDate(b);
  if (da === null || db === null) return da === db;
  return da.getTime() === db.getTime();
}

/** True for the attribute data types that are edited through a native date/time control. */
export function isDateDataType(dataType: string | null | undefined): dataType is 'date' | 'datetime' {
  return dataType === 'date' || dataType === 'datetime';
}

/** Wire -> control, picking the right shape for the data type. */
export function toDateInputValue(dataType: string | null | undefined, value: unknown): string {
  return dataType === 'date' ? toDateInput(value) : toDateTimeLocalInput(value);
}

/** Control -> wire, picking the right shape for the data type. */
export function fromDateInputValue(dataType: string | null | undefined, value: unknown): string | null {
  return dataType === 'date' ? fromDateInput(value) : fromDateTimeLocalInput(value);
}
