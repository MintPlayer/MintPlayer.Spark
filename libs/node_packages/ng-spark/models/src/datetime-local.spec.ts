import { describe, expect, it } from 'vitest';
import {
  fromDateInput,
  fromDateInputValue,
  fromDateTimeLocalInput,
  isDateDataType,
  parseWireDate,
  toDateInput,
  toDateInputValue,
  toDateTimeLocalInput,
  wireDatesEqual,
} from './datetime-local';

// These run in whatever zone the machine/CI is in, so every assertion is written to hold in ANY
// zone. Anything zone-specific is asserted as a relationship (the instant survived, the offsets
// agree) rather than as a literal string -- a test that only passes in Europe/Brussels is worse
// than no test, because it fails for a contributor somewhere else.

const localOffsetAt = (y: number, m: number, d: number, h = 0, min = 0) =>
  -new Date(y, m - 1, d, h, min).getTimezoneOffset();

describe('parseWireDate', () => {
  it('returns null for the empty cases rather than an Invalid Date', () => {
    expect(parseWireDate(null)).toBeNull();
    expect(parseWireDate(undefined)).toBeNull();
    expect(parseWireDate('')).toBeNull();
    expect(parseWireDate('not a date')).toBeNull();
    expect(parseWireDate({})).toBeNull();
  });

  it('parses a full ISO string with an offset', () => {
    const d = parseWireDate('2026-12-31T23:59:00-08:00');
    expect(d).not.toBeNull();
    expect(d!.toISOString()).toBe('2027-01-01T07:59:00.000Z');
  });
});

describe('toDateTimeLocalInput', () => {
  it('produces exactly the shape a datetime-local input accepts', () => {
    expect(toDateTimeLocalInput('2026-12-31T23:59:00-08:00')).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/);
  });

  it('renders the instant in the viewer zone, not the originating wall clock', () => {
    // The document says 23:59; what the control must show is that same instant locally.
    const instant = new Date('2026-12-31T23:59:00-08:00');
    const expected =
      `${instant.getFullYear()}-${String(instant.getMonth() + 1).padStart(2, '0')}-` +
      `${String(instant.getDate()).padStart(2, '0')}T${String(instant.getHours()).padStart(2, '0')}:` +
      `${String(instant.getMinutes()).padStart(2, '0')}`;
    expect(toDateTimeLocalInput('2026-12-31T23:59:00-08:00')).toBe(expected);
  });

  it('is empty for an absent value, so the control renders blank rather than Invalid Date', () => {
    expect(toDateTimeLocalInput(null)).toBe('');
    expect(toDateTimeLocalInput('')).toBe('');
    expect(toDateTimeLocalInput('rubbish')).toBe('');
  });

  it('does NOT pass a raw wire value through -- the regression this exists to prevent', () => {
    // Assigning the wire format straight to the control is silent: no throw, no warning, just a
    // blank field that overwrites the stored value on save.
    expect(toDateTimeLocalInput('2026-12-31T23:59:00-08:00')).not.toBe('2026-12-31T23:59:00-08:00');
  });
});

describe('fromDateTimeLocalInput', () => {
  it('emits a complete ISO-8601 string with an offset', () => {
    expect(fromDateTimeLocalInput('2026-07-04T12:30')).toMatch(
      /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[+-]\d{2}:\d{2}$/,
    );
  });

  it('preserves the typed wall clock verbatim', () => {
    const iso = fromDateTimeLocalInput('2026-07-04T12:30');
    expect(iso!.startsWith('2026-07-04T12:30:00')).toBe(true);
  });

  it('uses the offset for the ENTERED date, not the current one', () => {
    // The whole reason this builds a Date from parts. In a DST zone a January value and a July
    // value carry different offsets; using "now" would be wrong for one of them all year.
    const jan = fromDateTimeLocalInput('2026-01-15T12:00')!.slice(-6);
    const jul = fromDateTimeLocalInput('2026-07-15T12:00')!.slice(-6);

    const expectedJan = localOffsetAt(2026, 1, 15, 12);
    const expectedJul = localOffsetAt(2026, 7, 15, 12);
    const fmt = (m: number) =>
      `${m >= 0 ? '+' : '-'}${String(Math.floor(Math.abs(m) / 60)).padStart(2, '0')}:${String(Math.abs(m) % 60).padStart(2, '0')}`;

    expect(jan).toBe(fmt(expectedJan));
    expect(jul).toBe(fmt(expectedJul));
  });

  it('returns null for the empty cases', () => {
    expect(fromDateTimeLocalInput('')).toBeNull();
    expect(fromDateTimeLocalInput(null)).toBeNull();
    expect(fromDateTimeLocalInput('nonsense')).toBeNull();
  });
});

describe('the edit round trip', () => {
  // An untouched form must not move the value. This is the property that matters: open, save,
  // and the stored instant is unchanged.
  const cases = [
    '2026-12-31T23:59:00-08:00',
    '2026-07-04T12:00:00+05:45',
    '2026-01-01T00:00:00+00:00',
    '2026-03-15T08:30:00+02:00',
    '2026-09-30T18:45:00-03:30',
  ];

  it.each(cases)('%s survives wire -> control -> wire by instant', wire => {
    const control = toDateTimeLocalInput(wire);
    const back = fromDateTimeLocalInput(control);

    expect(back).not.toBeNull();
    // By instant, not by text: the offset is legitimately rewritten to the viewer's.
    expect(new Date(back!).getTime()).toBe(new Date(wire).getTime());
  });

  it.each(cases)('%s reports no change when nothing was edited', wire => {
    const back = fromDateTimeLocalInput(toDateTimeLocalInput(wire));
    expect(wireDatesEqual(back, wire)).toBe(true);
  });

  it('does report a change when the user actually edits', () => {
    const wire = '2026-07-04T12:00:00+02:00';
    const edited = fromDateTimeLocalInput(toDateTimeLocalInput(wire).replace(/T\d{2}:/, 'T23:'));
    expect(wireDatesEqual(edited, wire)).toBe(false);
  });
});

describe('an untouched save keeps the stored offset', () => {
  // Preserving the instant is the guarantee; preserving the offset on a save that changed nothing
  // is the courtesy that stops "open a record, press Save" from relabelling a Seattle timestamp as
  // a Brussels one. Verified end-to-end in the browser before this was added.
  it('sends back the original text when the instant is unchanged', () => {
    const stored = '2026-12-31T23:59:00-08:00';
    const converted = fromDateTimeLocalInput(toDateTimeLocalInput(stored));

    // The conversion is faithful by instant...
    expect(wireDatesEqual(converted, stored)).toBe(true);
    // ...but it is NOT the same text, which is exactly why the caller must not send it blindly.
    expect(converted).not.toBe(stored);

    const sent = wireDatesEqual(converted, stored) ? stored : converted;
    expect(sent).toBe(stored);
  });

  it('sends the new value when the instant did change', () => {
    const stored = '2026-12-31T23:59:00-08:00';
    const edited = fromDateTimeLocalInput('2026-06-01T09:00');

    expect(wireDatesEqual(edited, stored)).toBe(false);
    const sent = wireDatesEqual(edited, stored) ? stored : edited;
    expect(sent).toBe(edited);
  });
});

describe('date-only values', () => {
  it('produces the yyyy-MM-dd a date input accepts', () => {
    expect(toDateInput('2026-07-04T12:30:00+02:00')).toMatch(/^\d{4}-\d{2}-\d{2}$/);
  });

  it('round-trips to local midnight', () => {
    const iso = fromDateInput('2026-07-04');
    expect(iso!.startsWith('2026-07-04T00:00:00')).toBe(true);
  });

  it('rejects a value that is not a bare date', () => {
    expect(fromDateInput('2026-07-04T12:30')).toBeNull();
    expect(fromDateInput('')).toBeNull();
  });
});

describe('wireDatesEqual', () => {
  it('compares by instant, so the same moment in two offsets is equal', () => {
    expect(wireDatesEqual('2026-03-09T10:00:00+02:00', '2026-03-09T08:00:00Z')).toBe(true);
  });

  it('treats two absent values as equal and one absent as not', () => {
    expect(wireDatesEqual(null, '')).toBe(true);
    expect(wireDatesEqual(null, '2026-03-09T08:00:00Z')).toBe(false);
  });

  it('separates genuinely different instants', () => {
    expect(wireDatesEqual('2026-03-09T10:00:00+02:00', '2026-03-09T10:00:00Z')).toBe(false);
  });
});

describe('data-type dispatch', () => {
  it('recognises exactly the two native-control types', () => {
    expect(isDateDataType('date')).toBe(true);
    expect(isDateDataType('datetime')).toBe(true);
    expect(isDateDataType('string')).toBe(false);
    expect(isDateDataType(null)).toBe(false);
  });

  it('routes each type to the matching shape', () => {
    expect(toDateInputValue('date', '2026-07-04T12:30:00+02:00')).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    expect(toDateInputValue('datetime', '2026-07-04T12:30:00+02:00')).toMatch(
      /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/,
    );
    expect(fromDateInputValue('date', '2026-07-04')!.startsWith('2026-07-04T00:00:00')).toBe(true);
    expect(fromDateInputValue('datetime', '2026-07-04T12:30')!.startsWith('2026-07-04T12:30:00')).toBe(true);
  });
});
