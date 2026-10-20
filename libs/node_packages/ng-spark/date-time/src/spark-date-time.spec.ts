import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { currentLanguage } from '@mintplayer/ng-spark/models';
import {
  formatSparkDateTime,
  renderSparkDateTime,
  sparkDateTimeHint,
  sparkGenericZoneName,
  sparkOffsetLabel,
  sparkTimeZoneCity,
  wireOffsetMinutes,
} from './spark-date-time-format';
import { resolveSparkDateTimeDisplay } from './spark-date-time-display';
import { SparkDateTimeComponent } from './spark-date-time.component';
import { SPARK_TIME_ZONE_NAME_SOURCE, SparkTimeZoneNames } from './spark-time-zone-names';

/** Same options as the component's 'short' style, to build expected texts without hard-coding ICU spacing. */
const short = (date: Date, timeZone: string) =>
  new Intl.DateTimeFormat('en', { dateStyle: 'short', timeStyle: 'short', timeZone }).format(date);

describe('spark-date-time format (X16)', () => {
  beforeEach(() => currentLanguage.set('en'));

  it('reads the offset a wire value was written with', () => {
    expect(wireOffsetMinutes('2026-03-09T10:00:00+02:00')).toBe(120);
    expect(wireOffsetMinutes('2026-03-09T10:00:00.1234567-03:30')).toBe(-210);
    expect(wireOffsetMinutes('2026-03-09T08:00:00Z')).toBe(0);
    expect(wireOffsetMinutes('2026-03-09T08:00:00')).toBeNull();
    expect(wireOffsetMinutes('2026-03-09')).toBeNull();
    expect(wireOffsetMinutes(new Date())).toBeNull();
  });

  it('labels offsets like the server does', () => {
    expect(sparkOffsetLabel(0)).toBe('UTC');
    expect(sparkOffsetLabel(120)).toBe('UTC+02:00');
    expect(sparkOffsetLabel(-210)).toBe('UTC-03:30');
  });

  it('renders a fixed zone on the value\'s own date', () => {
    // 08:00Z on 9 March is 09:00 in Brussels (CET), not 10:00 (CEST).
    const value = '2026-03-09T08:00:00Z';
    expect(renderSparkDateTime(value, { zone: 'Europe/Brussels' })!.text).toBe(short(new Date(value), 'Europe/Brussels'));
    expect(renderSparkDateTime(value, { zone: 'Europe/Brussels' })!.text).toContain('9:00');
  });

  it('renders the stored wall clock with its offset, and the viewer\'s when there is no offset', () => {
    const stored = renderSparkDateTime('2026-03-09T10:00:00+02:00', 'stored')!.text;
    expect(stored).toContain('10:00');
    expect(stored.endsWith(' UTC+02:00')).toBe(true);

    const local = new Date(2026, 2, 9, 10, 0);
    expect(renderSparkDateTime(local, 'stored')!.text).toBe(renderSparkDateTime(local, 'viewer')!.text);
  });

  it('prints a non-date as itself and an absent value as nothing', () => {
    expect(formatSparkDateTime('not a date')).toBe('not a date');
    expect(formatSparkDateTime(null)).toBe('');
    expect(formatSparkDateTime('')).toBe('');
  });

  it('falls back to the viewer\'s zone for a zone this browser does not know', () => {
    const value = '2026-03-09T08:00:00Z';
    expect(renderSparkDateTime(value, { zone: 'Not/AZone' })).toBeNull();
    expect(formatSparkDateTime(value, { zone: 'Not/AZone' })).toBe(formatSparkDateTime(value, 'viewer'));
  });

  it('shows the hint only when the wall clocks differ at that instant (D35)', () => {
    const value = '2026-07-04T19:00:00Z';
    const hint = sparkDateTimeHint(value, { zone: 'Europe/Brussels' }, 'Europe/London');
    expect(hint!.text).toBe(short(new Date(value), 'Europe/London'));

    // Paris and Brussels give the same wall clock: hidden, whatever the names.
    expect(sparkDateTimeHint(value, { zone: 'Europe/Brussels' }, 'Europe/Paris')).toBeNull();
    // London matches Lisbon in summer; by offset on that date, not by name.
    expect(sparkDateTimeHint(value, { zone: 'Europe/Lisbon' }, 'Europe/London')).toBeNull();
    expect(sparkDateTimeHint(value, 'viewer', null)).toBeNull();
  });

  it('derives the English city and finds the platform\'s generic name (D36 steps 3 and 2)', () => {
    expect(sparkTimeZoneCity('Europe/Brussels')).toBe('Brussels');
    expect(sparkTimeZoneCity('America/Argentina/Buenos_Aires')).toBe('Buenos Aires');
    expect(sparkTimeZoneCity('UTC')).toBe('UTC');
    // S23: Chromium, Firefox and Node (ICU 78) all localize longGeneric per call.
    expect(sparkGenericZoneName('Europe/Brussels', 'nl')).toBe('Midden-Europese tijd');
    expect(sparkGenericZoneName('Not/AZone', 'nl')).toBeNull();
  });
});

describe('resolveSparkDateTimeDisplay', () => {
  it('is the viewer\'s zone without a rule', () => {
    expect(resolveSparkDateTimeDisplay(null, null)).toEqual({ mode: 'viewer', alsoIn: null });
  });

  it('reads a zoneFrom from the row in every shape valueFor understands', () => {
    const rule = { primary: 'viewer' as const, hint: { zoneFrom: 'VenueTimeZoneId' } };
    expect(resolveSparkDateTimeDisplay(rule, { VenueTimeZoneId: 'Europe/London' })).toEqual({ mode: 'viewer', alsoIn: 'Europe/London' });
    expect(resolveSparkDateTimeDisplay(rule, { id: 'r', values: [{ key: 'VenueTimeZoneId', value: 'Asia/Tokyo' }] } as any).alsoIn).toBe('Asia/Tokyo');
    expect(resolveSparkDateTimeDisplay(rule, { VenueTimeZoneId: '' }).alsoIn).toBeNull();
    expect(resolveSparkDateTimeDisplay(rule, {}).alsoIn).toBeNull();
  });

  it('maps stored and a fixed zone', () => {
    expect(resolveSparkDateTimeDisplay({ primary: 'stored' }, null).mode).toBe('stored');
    expect(resolveSparkDateTimeDisplay({ primary: { zone: 'Asia/Tokyo' }, hint: 'stored' }, null)).toEqual({ mode: { zone: 'Asia/Tokyo' }, alsoIn: 'stored' });
  });
});

describe('SparkTimeZoneNames (D36)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('answers the platform name at once, then the source\'s city, asking the source once per language', async () => {
    let calls = 0;
    TestBed.configureTestingModule({
      providers: [{ provide: SPARK_TIME_ZONE_NAME_SOURCE, useValue: { names: async () => { calls++; return { 'Europe/Brussels': 'Brussel' }; } } }],
    });
    const names = TestBed.inject(SparkTimeZoneNames);

    const label = names.label('Europe/Brussels', 'nl');
    expect(label()).toBe('Midden-Europese tijd');
    const other = names.label('Asia/Tokyo', 'nl');
    await new Promise(resolve => setTimeout(resolve, 0));

    expect(label()).toBe('Brussel');
    expect(other()).toBe(sparkGenericZoneName('Asia/Tokyo', 'nl'));
    expect(calls).toBe(1);
  });

  it('keeps the fallback when the source fails', async () => {
    TestBed.configureTestingModule({
      providers: [{ provide: SPARK_TIME_ZONE_NAME_SOURCE, useValue: { names: () => Promise.reject(new Error('down')) } }],
    });
    const label = TestBed.inject(SparkTimeZoneNames).label('Europe/Brussels', 'en');
    await new Promise(resolve => setTimeout(resolve, 0));
    expect(label()).toBe('Central European Time');
  });
});

describe('SparkDateTimeComponent', () => {
  beforeEach(() => currentLanguage.set('en'));
  afterEach(() => TestBed.resetTestingModule());

  function render(inputs: Record<string, unknown>): HTMLElement {
    const fixture = TestBed.createComponent(SparkDateTimeComponent);
    for (const [k, v] of Object.entries(inputs)) fixture.componentRef.setInput(k, v);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('renders the main time in a <time> with the instant, and the hint with its zone label', () => {
    const value = '2026-07-04T19:00:00Z';
    const el = render({ value, mode: { zone: 'Europe/Brussels' }, alsoIn: 'Europe/London' });

    expect(el.querySelector('time')!.getAttribute('datetime')).toBe('2026-07-04T19:00:00.000Z');
    expect(el.querySelector('time')!.textContent!.trim()).toBe(`${short(new Date(value), 'Europe/Brussels')} Central European Time`);
    expect(el.querySelector('.spark-date-time-hint')!.textContent!.trim())
      .toBe(`(${short(new Date(value), 'Europe/London')} ${sparkGenericZoneName('Europe/London', 'en')})`);
  });

  it('hides the hint when the wall clocks match', () => {
    const el = render({ value: '2026-07-04T19:00:00Z', mode: { zone: 'Europe/Brussels' }, alsoIn: 'Europe/Paris' });
    expect(el.querySelector('.spark-date-time-hint')).toBeNull();
  });

  it('prints a value that is not a date as itself', () => {
    const el = render({ value: 'not a date' });
    expect(el.textContent!.trim()).toBe('not a date');
    expect(el.querySelector('time')).toBeNull();
  });
});
