// The check-in rules of GB3 (commerce PRD §4.11): the TypeScript twin of the server's CheckInRules
// (libs/commerce/MintPlayer.Spark.Commerce.Ticketing.Tickets/CheckIn/CheckInRules.cs), so a door device offline decides
// exactly as the server does online. Pure; the specs mirror CheckInRulesTests.

import { SparkScannerEntry, SparkScannerList, SparkTicketDate } from './generated/contracts';

/** Scan outcomes; permanent (translated as `ticketingTickets.scan.{code}`). */
export const TicketScanResult = {
  admitted: 'admitted',
  exited: 'exited',
  alreadyIn: 'alreadyIn',
  notInside: 'notInside',
  notOnList: 'notOnList',
  wrongDate: 'wrongDate',
  revoked: 'revoked',
  onHold: 'onHold',
  unknown: 'unknown',
  noReentry: 'noReentry',
} as const;

export type TicketScanResultCode = (typeof TicketScanResult)[keyof typeof TicketScanResult];

export type TicketScanDirection = 'In' | 'Out';

/** How long before doors (or the start) a date's scans begin to count as that date: 6 hours. */
export const CHECK_IN_BEFORE_DOORS_MS = 6 * 3600_000;

/** How long after the start a date without an end still counts: 12 hours. */
export const CHECK_IN_AFTER_START_WITHOUT_END_MS = 12 * 3600_000;

/** Whether `now` lies in `date`'s check-in window. */
export function isDateOn(date: Pick<SparkTicketDate, 'startsAt' | 'doorsOpenAt' | 'endsAt'>, now: number): boolean {
  const start = Date.parse(date.startsAt);
  const opens = Date.parse(date.doorsOpenAt ?? date.startsAt) - CHECK_IN_BEFORE_DOORS_MS;
  const closes = date.endsAt ? Date.parse(date.endsAt) : start + CHECK_IN_AFTER_START_WITHOUT_END_MS;
  return now >= opens && now <= closes;
}

/**
 * The date a scan counts for: the device's own date when it has one, else the first date (by start) of the list and the
 * ticket whose window holds `now`; `null` when none is on.
 */
export function checkInDateFor(
  list: Pick<SparkScannerList, 'dateIds'>,
  ticketDateIds: readonly string[] | null,
  dates: readonly SparkTicketDate[],
  preferred: string | null | undefined,
  now: number,
): string | null {
  if (preferred) return preferred;
  const ordered = [...dates].sort((a, b) => Date.parse(a.startsAt) - Date.parse(b.startsAt));
  for (const date of ordered) {
    if (list.dateIds.length > 0 && !list.dateIds.includes(date.id)) continue;
    if (ticketDateIds && !ticketDateIds.includes(date.id)) continue;
    if (isDateOn(date, now)) return date.id;
  }
  return null;
}

/** The refusal of a scan before presence is looked at, or `null` when the ticket may pass. */
export function checkInRefusal(
  entry: Pick<SparkScannerEntry, 'status' | 'dateIds' | 'ticketTypeId'>,
  list: SparkScannerList,
  dateId: string | null,
  direction: TicketScanDirection,
): TicketScanResultCode | null {
  if (entry.status === 'Revoked') return TicketScanResult.revoked;
  if (entry.status === 'OnHold') return TicketScanResult.onHold;
  if (!dateId || !entry.dateIds.includes(dateId) || (list.dateIds.length > 0 && !list.dateIds.includes(dateId))) return TicketScanResult.wrongDate;
  const zone = list.kind === 'Zone';
  if (list.ticketTypeIds.length > 0 ? !list.ticketTypeIds.includes(entry.ticketTypeId) : zone) return TicketScanResult.notOnList;
  if (direction === 'Out' && !list.allowReentry) return TicketScanResult.noReentry;
  return null;
}

/** One recorded scan of a ticket on a list and date. */
export interface PresenceEvent {
  scanId: string;
  direction: TicketScanDirection;
  /** Unix milliseconds. */
  at: number;
  doubleEntry?: boolean;
  conflictScanId?: string | null;
}

/**
 * Recomputes double-entry flags and the inside state of one list and date in time order (ties by scan id, ordinal),
 * exactly like the server's `CheckInRules.Recompute`: so the order in which devices sync does not matter (S9).
 */
export function recomputePresence(events: PresenceEvent[]): { inside: boolean; entries: number; events: PresenceEvent[] } {
  const sorted = [...events].sort((a, b) => a.at - b.at || (a.scanId < b.scanId ? -1 : a.scanId > b.scanId ? 1 : 0));
  let inside = false;
  let entries = 0;
  let lastIn: PresenceEvent | null = null;
  for (const e of sorted) {
    e.doubleEntry = false;
    e.conflictScanId = null;
    if (e.direction === 'In') {
      if (inside) {
        e.doubleEntry = true;
        e.conflictScanId = lastIn?.scanId ?? null;
        continue;
      }
      inside = true;
      entries++;
      lastIn = e;
    } else if (inside) {
      inside = false;
    }
  }
  return { inside, entries, events: sorted };
}

/**
 * Decides an offline scan against the device's copy of the ticket (its `inside` and `used` dates on this list) and
 * updates that copy. A second "in" is reported as `alreadyIn` at once; the server confirms the flag on sync, when every
 * device's scans are known.
 */
export function applyOfflineScan(
  entry: SparkScannerEntry,
  list: SparkScannerList,
  dateId: string | null,
  direction: TicketScanDirection,
): TicketScanResultCode {
  const refusal = checkInRefusal(entry, list, dateId, direction);
  if (refusal) return refusal;
  const date = dateId!;
  const inside = entry.inside.includes(date);
  if (direction === 'In') {
    if (inside) return TicketScanResult.alreadyIn;
    entry.inside = [...entry.inside, date];
    if (!entry.used.includes(date)) entry.used = [...entry.used, date];
    return TicketScanResult.admitted;
  }
  if (!inside) return TicketScanResult.notInside;
  entry.inside = entry.inside.filter(d => d !== date);
  return TicketScanResult.exited;
}
