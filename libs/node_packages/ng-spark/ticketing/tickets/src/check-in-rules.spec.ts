import { describe, expect, it } from 'vitest';
import { SparkScannerEntry, SparkScannerList, SparkTicketDate } from './generated/contracts';
import { applyOfflineScan, checkInDateFor, checkInRefusal, PresenceEvent, recomputePresence, TicketScanResult } from './check-in-rules';

// Commerce #521 M15 (GB3, S9): the TypeScript twin of CheckInRules; the cases mirror CheckInRulesTests.

const T0 = Date.parse('2026-07-01T18:00:00Z');
const minutes = (n: number) => T0 + n * 60_000;

function entry(overrides: Partial<SparkScannerEntry> = {}): SparkScannerEntry {
  return {
    code: '0123456789ABCDEF', salt: '', hash: '', iterations: 1, ticketTypeId: 'TicketTypes/standard',
    dateIds: ['TicketEventDates/1'], status: 'Valid', inside: [], used: [], ...overrides,
  };
}

function list(overrides: Partial<SparkScannerList> = {}): SparkScannerList {
  return { id: 'L', name: 'Gate A', kind: 'Gate', dateIds: [], ticketTypeIds: [], allowReentry: false, ...overrides };
}

const scan = (scanId: string, at: number, direction: 'In' | 'Out' = 'In'): PresenceEvent => ({ scanId, at: minutes(at), direction });

describe('check-in rules', () => {
  it('flag a second entry without re-entry', () => {
    const e = entry();
    const l = list();
    expect(applyOfflineScan(e, l, 'TicketEventDates/1', 'In')).toBe(TicketScanResult.admitted);
    expect(applyOfflineScan(e, l, 'TicketEventDates/1', 'In')).toBe(TicketScanResult.alreadyIn);
    expect(applyOfflineScan(e, l, 'TicketEventDates/1', 'Out')).toBe(TicketScanResult.noReentry);
  });

  it('toggle with re-entry', () => {
    const e = entry();
    const l = list({ allowReentry: true });
    expect(applyOfflineScan(e, l, 'TicketEventDates/1', 'In')).toBe(TicketScanResult.admitted);
    expect(applyOfflineScan(e, l, 'TicketEventDates/1', 'Out')).toBe(TicketScanResult.exited);
    expect(applyOfflineScan(e, l, 'TicketEventDates/1', 'Out')).toBe(TicketScanResult.notInside);
    expect(applyOfflineScan(e, l, 'TicketEventDates/1', 'In')).toBe(TicketScanResult.admitted);
    expect(e.used).toEqual(['TicketEventDates/1']);
  });

  it('recompute the same flags whichever device synced first (S9)', () => {
    const bFirst = recomputePresence([scan('b', 3), scan('a', 0)]);
    const aFirst = recomputePresence([scan('a', 0), scan('b', 3)]);
    const flags = (r: ReturnType<typeof recomputePresence>) => r.events.map(e => [e.scanId, e.doubleEntry]);
    expect(flags(bFirst)).toEqual(flags(aFirst));
    expect(flags(aFirst)).toEqual([['a', false], ['b', true]]);
    expect(aFirst.events[1].conflictScanId).toBe('a');
    expect(aFirst.entries).toBe(1);

    const toggled = recomputePresence([scan('in1', 0), scan('out1', 10, 'Out'), scan('in2', 20), scan('in3', 25)]);
    expect(toggled.entries).toBe(2);
    expect(toggled.inside).toBe(true);
    expect(toggled.events.find(e => e.scanId === 'in3')?.doubleEntry).toBe(true);
  });

  it('refuse before presence', () => {
    const l = list();
    expect(checkInRefusal(entry(), l, 'TicketEventDates/1', 'In')).toBeNull();
    expect(checkInRefusal(entry(), l, 'TicketEventDates/9', 'In')).toBe(TicketScanResult.wrongDate);
    expect(checkInRefusal(entry(), l, null, 'In')).toBe(TicketScanResult.wrongDate);
    expect(checkInRefusal(entry({ status: 'Revoked' }), l, 'TicketEventDates/1', 'In')).toBe(TicketScanResult.revoked);
    expect(checkInRefusal(entry({ status: 'OnHold' }), l, 'TicketEventDates/1', 'In')).toBe(TicketScanResult.onHold);
    expect(checkInRefusal(entry(), list({ dateIds: ['TicketEventDates/2'] }), 'TicketEventDates/1', 'In')).toBe(TicketScanResult.wrongDate);
  });

  it('admit only named types on a zone', () => {
    const vip = list({ kind: 'Zone', ticketTypeIds: ['TicketTypes/vip'] });
    expect(checkInRefusal(entry(), vip, 'TicketEventDates/1', 'In')).toBe(TicketScanResult.notOnList);
    expect(checkInRefusal(entry({ ticketTypeId: 'TicketTypes/vip' }), vip, 'TicketEventDates/1', 'In')).toBeNull();
    expect(checkInRefusal(entry({ ticketTypeId: 'TicketTypes/vip' }), list({ kind: 'Zone' }), 'TicketEventDates/1', 'In')).toBe(TicketScanResult.notOnList);
  });

  it('pick the date whose window is on', () => {
    const d1: SparkTicketDate = { id: 'TicketEventDates/1', startsAt: new Date(T0).toISOString(), doorsOpenAt: new Date(T0 - 3600_000).toISOString(), endsAt: new Date(T0 + 5 * 3600_000).toISOString() };
    const d2: SparkTicketDate = { id: 'TicketEventDates/2', startsAt: new Date(T0 + 24 * 3600_000).toISOString() };
    const pass = ['TicketEventDates/1', 'TicketEventDates/2'];
    expect(checkInDateFor(list(), pass, [d2, d1], null, T0 - 6 * 3600_000)).toBe('TicketEventDates/1');
    expect(checkInDateFor(list(), pass, [d1, d2], null, T0 + 4 * 3600_000)).toBe('TicketEventDates/1');
    expect(checkInDateFor(list(), pass, [d1, d2], null, T0 + 12 * 3600_000)).toBeNull();
    expect(checkInDateFor(list(), pass, [d1, d2], null, T0 + 35 * 3600_000)).toBe('TicketEventDates/2');
    expect(checkInDateFor(list(), pass, [d1, d2], 'TicketEventDates/2', T0)).toBe('TicketEventDates/2');
    expect(checkInDateFor(list({ dateIds: ['TicketEventDates/2'] }), pass, [d1, d2], null, T0)).toBeNull();
    expect(checkInDateFor(list(), ['TicketEventDates/2'], [d1, d2], null, T0)).toBeNull();
  });
});
