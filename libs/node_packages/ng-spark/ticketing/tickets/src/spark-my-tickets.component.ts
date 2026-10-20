import { ChangeDetectionStrategy, Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { SparkDateTimeComponent } from '@mintplayer/ng-spark/date-time';
import { ResolveTranslationPipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkTicketView } from './generated/contracts';
import { SparkTicketsService } from './spark-tickets.service';

/** The ticket page's URL for `code` from a path pattern with `{code}` (the server's `Delivery:TicketPagePath`). */
export function ticketPageUrl(pattern: string, code: string): string {
  return pattern.replace('{code}', encodeURIComponent(code));
}

/** Tickets whose first date has not ended come first (soonest first), then the past ones (latest first). */
export function sortTickets(tickets: SparkTicketView[], nowMs: number): SparkTicketView[] {
  const start = (t: SparkTicketView) => (t.dates[0] ? Date.parse(t.dates[0].startsAt) : Number.POSITIVE_INFINITY);
  const end = (t: SparkTicketView) => {
    const last = t.dates[t.dates.length - 1];
    return last ? Date.parse(last.endsAt ?? last.startsAt) : Number.POSITIVE_INFINITY;
  };
  const upcoming = tickets.filter((t) => end(t) >= nowMs).sort((a, b) => start(a) - start(b));
  const past = tickets.filter((t) => end(t) < nowMs).sort((a, b) => start(b) - start(a));
  return [...upcoming, ...past];
}

/**
 * The account area's tickets (GB10, PRD §4.14): every ticket the signed-in account (or the guest visitor) holds,
 * upcoming first, each with a link to its ticket page and a PDF re-download (GC7 template). A holder opens a ticket
 * without its secret: the server checks the holder.
 *
 * `<spark-my-tickets ticketPath="/tickets/{code}" />`
 */
@Component({
  selector: 'spark-my-tickets',
  imports: [TranslateKeyPipe, ResolveTranslationPipe, SparkDateTimeComponent],
  templateUrl: './spark-my-tickets.component.html',
  styleUrl: './spark-my-tickets.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkMyTicketsComponent implements OnInit {
  private readonly tickets = inject(SparkTicketsService);

  /** The ticket page's path pattern; matches the server's `Delivery:TicketPagePath` by default. */
  ticketPath = input('/tickets/{code}');

  protected readonly list = signal<SparkTicketView[]>([]);
  protected readonly loaded = signal(false);
  protected readonly busyCode = signal<string | null>(null);
  protected readonly failed = signal(false);
  protected readonly sorted = computed(() => sortTickets(this.list(), Date.now()));

  async ngOnInit(): Promise<void> {
    try {
      this.list.set((await this.tickets.mine()).tickets);
    } catch {
      this.failed.set(true);
    } finally {
      this.loaded.set(true);
    }
  }

  protected href(ticket: SparkTicketView): string {
    return ticketPageUrl(this.ticketPath(), ticket.code);
  }

  protected async download(ticket: SparkTicketView): Promise<void> {
    this.busyCode.set(ticket.code);
    try {
      const blob = await this.tickets.pdf({ code: ticket.code, secret: null });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `ticket-${ticket.code}.pdf`;
      a.click();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch {
      this.failed.set(true);
    } finally {
      this.busyCode.set(null);
    }
  }
}
