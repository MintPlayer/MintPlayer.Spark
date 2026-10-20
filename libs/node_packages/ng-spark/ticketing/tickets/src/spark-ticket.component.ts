import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { formatMoney, Money } from '@mintplayer/ng-spark/models';
import { ResolveTranslationPipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkDateTimeComponent } from '@mintplayer/ng-spark/date-time';
import { SparkTicketCredential, SparkTicketView } from './generated/contracts';
import { resaleCurrency, SparkTicketsService, ticketCredentialFromLink, ticketRefusalKey, ticketRefusalOf } from './spark-tickets.service';

/**
 * The holder's ticket page (commerce #521 M15, PRD §4.11): the QR code once the seller's lead time has passed (D11),
 * else "available on …"; the dates in the viewer's zone with the venue-local hint (D33); the "ID check" note (GB12)
 * and the "on hold" state (GB1); the PDF; naming the holder, transfer and face-value resale until the freeze (D30).
 * The event's template colours apply (GC7). The secret comes from the link's fragment (`#k=`), or from the account
 * that holds the ticket.
 *
 * `<spark-ticket [code]="code" />` on the route `/tickets/:code`.
 */
@Component({
  selector: 'spark-ticket',
  imports: [TranslateKeyPipe, ResolveTranslationPipe, SparkDateTimeComponent],
  templateUrl: './spark-ticket.component.html',
  styleUrl: './spark-ticket.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkTicketComponent {
  private readonly tickets = inject(SparkTicketsService);
  private readonly sanitizer = inject(DomSanitizer);

  /** The ticket code (the route value). */
  code = input.required<string>();

  /** The secret, when the host read it elsewhere; by default the page's `#k=` fragment. */
  secret = input<string | null>(null);

  protected readonly ticket = signal<SparkTicketView | null>(null);
  protected readonly loaded = signal(false);
  protected readonly busy = signal(false);
  protected readonly refusal = signal<string | null>(null);
  protected readonly holderName = signal('');
  protected readonly transferEmail = signal('');
  protected readonly transferName = signal('');
  protected readonly resalePrice = signal('');

  private credential: SparkTicketCredential = { code: '' };

  /** The QR code: an SVG the server's QR renderer wrote from the payload's restricted alphabet (base 32 + base64url). */
  protected readonly qr = computed<SafeHtml | null>(() => {
    const svg = this.ticket()?.qrSvg;
    return svg ? this.sanitizer.bypassSecurityTrustHtml(svg) : null;
  });

  protected readonly style = computed(() => {
    const template = this.ticket()?.template;
    return {
      '--spark-ticket-accent': template?.accentColor ?? null,
      '--spark-ticket-accent-text': template?.accentTextColor ?? null,
    };
  });

  constructor() {
    effect(() => {
      const fragment = typeof location === 'undefined' ? '' : location.hash;
      const fromLink = ticketCredentialFromLink(this.code(), fragment);
      this.credential = { code: fromLink.code, secret: this.secret() ?? fromLink.secret };
      void this.load();
    });
  }

  protected money(value: Money | null | undefined): string {
    return value ? formatMoney(value) : '';
  }

  private async load(): Promise<void> {
    this.loaded.set(false);
    try {
      const view = await this.tickets.open(this.credential);
      this.ticket.set(view);
      this.holderName.set(view.holderName ?? '');
    } catch (error) {
      this.ticket.set(null);
      this.fail(error);
    } finally {
      this.loaded.set(true);
    }
  }

  /** After a reissue the view carries the new secret: the page keeps working, the old link does not. */
  private adopt(view: SparkTicketView): void {
    this.ticket.set(view);
    const secret = view.payload ? view.payload.slice(view.payload.lastIndexOf('.') + 1) : null;
    if (secret) this.credential = { code: view.code, secret };
  }

  private fail(error: unknown): void {
    const refusal = ticketRefusalOf(error);
    this.refusal.set(refusal ? ticketRefusalKey(refusal.code) : 'ticketingTickets.refusal.notFound');
  }

  private async run(work: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.refusal.set(null);
    try {
      await work();
    } catch (error) {
      this.fail(error);
    } finally {
      this.busy.set(false);
    }
  }

  protected downloadPdf(): Promise<void> {
    return this.run(async () => {
      const blob = await this.tickets.pdf(this.credential);
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `ticket-${this.credential.code}.pdf`;
      a.click();
      setTimeout(() => URL.revokeObjectURL(url), 10_000);
    });
  }

  protected saveHolder(): Promise<void> {
    return this.run(async () => this.adopt(await this.tickets.setHolder(this.credential, this.holderName().trim())));
  }

  protected startTransfer(): Promise<void> {
    return this.run(async () => {
      await this.tickets.transfer(this.credential, this.transferEmail().trim(), this.transferName().trim() || null);
      await this.load();
    });
  }

  protected cancelTransfer(): Promise<void> {
    return this.run(async () => {
      await this.tickets.cancelTransfer(this.credential);
      await this.load();
    });
  }

  protected resell(): Promise<void> {
    const ticket = this.ticket();
    const amount = Number(this.resalePrice().replace(',', '.'));
    const currency = resaleCurrency(ticket);
    if (!ticket || !currency || !Number.isFinite(amount) || amount <= 0) return Promise.resolve();
    return this.run(async () => {
      await this.tickets.resell(this.credential, { Amount: amount, Currency: currency });
      await this.load();
    });
  }

  protected withdrawResale(): Promise<void> {
    return this.run(async () => {
      await this.tickets.withdrawResale(this.credential);
      await this.load();
    });
  }
}
