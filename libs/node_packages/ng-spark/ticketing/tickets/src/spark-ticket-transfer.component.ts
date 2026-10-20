import { ChangeDetectionStrategy, Component, effect, inject, input, output, signal } from '@angular/core';
import { ResolveTranslationPipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkDateTimeComponent } from '@mintplayer/ng-spark/date-time';
import { SparkTicketTransferOffer, SparkTicketView } from './generated/contracts';
import { SparkTicketsService, ticketRefusalKey, ticketRefusalOf, transferTokenFromLink } from './spark-tickets.service';

/**
 * Accepting a transferred ticket (commerce #521 M15, D30, PRD §4.11 round 5): shows what is offered (never the giver's
 * personal data), says that accepting reissues the ticket and erases the previous holder from it, asks a name when the
 * event names its tickets, and emits the new ticket. The token comes from the link's fragment (`#t=`).
 *
 * `<spark-ticket-transfer [transferId]="id" (accepted)="open($event)" />` on the route `/tickets/transfer/:id`.
 */
@Component({
  selector: 'spark-ticket-transfer',
  imports: [TranslateKeyPipe, ResolveTranslationPipe, SparkDateTimeComponent],
  templateUrl: './spark-ticket-transfer.component.html',
  styleUrl: './spark-ticket-transfer.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkTicketTransferComponent {
  private readonly tickets = inject(SparkTicketsService);

  /** The transfer id (the route value). */
  transferId = input.required<string>();

  /** The ticket, reissued to the caller; its payload carries the new secret. */
  accepted = output<SparkTicketView>();

  protected readonly offer = signal<SparkTicketTransferOffer | null>(null);
  protected readonly name = signal('');
  protected readonly busy = signal(false);
  protected readonly refusal = signal<string | null>(null);

  private token: string | null = null;

  constructor() {
    effect(() => {
      const id = this.transferId();
      this.token = transferTokenFromLink(typeof location === 'undefined' ? '' : location.hash);
      if (!this.token) {
        this.refusal.set('ticketingTickets.refusal.transferClosed');
        return;
      }
      void this.tickets.transferOffer(id, this.token).then(o => this.offer.set(o), e => this.fail(e));
    });
  }

  protected async accept(): Promise<void> {
    const offer = this.offer();
    if (!offer || !this.token) return;
    this.busy.set(true);
    this.refusal.set(null);
    try {
      this.accepted.emit(await this.tickets.acceptTransfer(offer.transferId, this.token, this.name().trim() || null));
    } catch (error) {
      this.fail(error);
    } finally {
      this.busy.set(false);
    }
  }

  private fail(error: unknown): void {
    const refusal = ticketRefusalOf(error);
    this.refusal.set(refusal ? ticketRefusalKey(refusal.code) : 'ticketingTickets.refusal.transferClosed');
  }
}
