import { describe, expect, it } from 'vitest';
import { SparkTicketView } from './generated/contracts';
import { sortTickets, ticketPageUrl } from './spark-my-tickets.component';

// M17 / GB10: the account area's tickets.

const ticket = (code: string, startsAt: string, endsAt?: string) =>
  ({ code, dates: [{ id: 'd', startsAt, endsAt: endsAt ?? null }] }) as unknown as SparkTicketView;

describe('my tickets', () => {
  const now = Date.parse('2026-10-20T12:00:00Z');

  it('lists upcoming tickets soonest first, then past ones latest first', () => {
    const sorted = sortTickets([
      ticket('PAST1', '2026-01-01T20:00:00Z'),
      ticket('LATER', '2026-12-01T20:00:00Z'),
      ticket('PAST2', '2026-06-01T20:00:00Z'),
      ticket('SOON', '2026-10-21T20:00:00Z'),
      // Started but not ended: still upcoming.
      ticket('NOW', '2026-10-20T10:00:00Z', '2026-10-20T23:00:00Z'),
    ], now);
    expect(sorted.map((t) => t.code)).toEqual(['NOW', 'SOON', 'LATER', 'PAST2', 'PAST1']);
  });

  it('builds the ticket page URL from the server pattern', () => {
    expect(ticketPageUrl('/tickets/{code}', 'AB12')).toBe('/tickets/AB12');
  });
});
