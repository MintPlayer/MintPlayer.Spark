import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { formatMoney } from '@mintplayer/ng-spark/models';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkDateTimeComponent } from '@mintplayer/ng-spark/date-time';
import { SparkPreSelection, SparkPreSelectLine, SparkPublicEvent } from './generated/contracts';
import {
  SparkTicketingEventsService,
  TICKETING_PASS_DATE,
  ticketingRefusalKey,
  ticketingRefusalOf,
  ticketItemId,
} from './spark-ticketing-events.service';

/** One offer the buyer can pre-select: a date × ticket type × price category of the phase. */
export interface SparkPreSelectOffer {
  itemId: string;
  dateId: string;
  label: string;
  price: unknown;
}

/**
 * PreSelectOrder (D25, PRD §4.11): while the buyer waits in the queue of a phase, they list what they want (ticket type
 * × price category × quantity, per date) with ranked alternatives. When the waiting room admits them, the host passes
 * the admission token in `[admissionToken]`: the pre-selection is held at once (clamped to what is left and to the
 * buyer's limit), and "Release everything" gives it back in one click. The presence check is the waiting room's.
 *
 * `<spark-ticket-preselect [event]="event" [phaseId]="phase.id" [admissionToken]="token()" (held)="goToBasket()" />`
 */
@Component({
  selector: 'spark-ticket-preselect',
  imports: [TranslateKeyPipe, SparkDateTimeComponent],
  templateUrl: './spark-ticket-preselect.component.html',
  styleUrl: './spark-ticket-preselect.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkTicketPreselectComponent {
  private readonly events = inject(SparkTicketingEventsService);
  private readonly lang = inject(SparkLanguageService);

  /** The public event (`SparkTicketingEventsService.publicEvent`). */
  event = input.required<SparkPublicEvent>();

  /** The queued phase. */
  phaseId = input.required<string>();

  /** The waiting room's admission token: once set, the pre-selection is held. */
  admissionToken = input<string | null>(null);

  /** The pre-selection was held; the basket holds it now. */
  held = output<SparkPreSelection>();

  protected readonly lines = signal<SparkPreSelectLine[]>([]);
  protected readonly selection = signal<SparkPreSelection | null>(null);
  protected readonly busy = signal(false);
  protected readonly refusal = signal<string | null>(null);

  /** Every offer of the phase, one per date (a pass once). */
  protected readonly offers = computed<SparkPreSelectOffer[]>(() => {
    const ev = this.event();
    const phase = ev.phases.find((p) => p.id === this.phaseId());
    if (!phase) return [];
    const offers: SparkPreSelectOffer[] = [];
    for (const price of phase.prices) {
      const type = ev.ticketTypes.find((t) => t.id === price.ticketTypeId);
      if (!type || type.kind === 'AddOn') continue;
      const name = this.lang.resolve(type.name) || type.id;
      const label = price.category ? `${name} · ${price.category}` : name;
      if (type.kind === 'Pass') {
        offers.push({ itemId: ticketItemId(phase.id, type.id, price.category, TICKETING_PASS_DATE), dateId: TICKETING_PASS_DATE, label, price: price.finalPrice });
        continue;
      }
      for (const dateId of phase.dateIds) {
        if (type.dateIds.length > 0 && !type.dateIds.includes(dateId)) continue;
        offers.push({ itemId: ticketItemId(phase.id, type.id, price.category, dateId), dateId, label, price: price.finalPrice });
      }
    }
    return offers;
  });

  constructor() {
    effect(() => {
      const phaseId = this.phaseId();
      void this.events.preSelection(phaseId).then((s) => {
        this.selection.set(s);
        this.lines.set(s?.lines.length ? s.lines.map((l) => ({ alternatives: l.alternatives.map((a) => ({ ...a })) })) : []);
      }, () => this.selection.set(null));
    });
    effect(() => {
      const token = this.admissionToken();
      if (token) void this.apply(token);
    });
  }

  /** The start of an offer's date (null for a pass). */
  protected startOf(itemId: string): string | null {
    const dateId = this.offers().find((o) => o.itemId === itemId)?.dateId;
    return this.event().dates.find((d) => d.id === dateId)?.startsAt ?? null;
  }

  /** The venue zone of an offer's date: the secondary "in Brussels" rendering (D35). */
  protected zoneOfItem(itemId: string): string | null {
    const dateId = this.offers().find((o) => o.itemId === itemId)?.dateId;
    return this.event().dates.find((d) => d.id === dateId)?.venueTimeZoneId ?? null;
  }

  protected money(value: unknown): string {
    return value ? formatMoney(value) : '';
  }

  protected addLine(): void {
    const first = this.offers()[0];
    if (!first) return;
    this.lines.update((lines) => [...lines, { alternatives: [{ itemId: first.itemId, quantity: 1 }] }]);
  }

  protected addAlternative(line: number): void {
    const first = this.offers()[0];
    if (!first) return;
    this.lines.update((lines) => lines.map((l, i) => i === line ? { alternatives: [...l.alternatives, { itemId: first.itemId, quantity: l.alternatives[0]?.quantity ?? 1 }] } : l));
  }

  protected removeAlternative(line: number, alternative: number): void {
    this.lines.update((lines) => lines
      .map((l, i) => i === line ? { alternatives: l.alternatives.filter((_, a) => a !== alternative) } : l)
      .filter((l) => l.alternatives.length > 0));
  }

  protected setItem(line: number, alternative: number, itemId: string): void {
    this.update(line, alternative, (a) => ({ ...a, itemId }));
  }

  protected setQuantity(line: number, alternative: number, value: string): void {
    const quantity = Math.max(1, Math.min(20, Number.parseInt(value, 10) || 1));
    this.update(line, alternative, (a) => ({ ...a, quantity }));
  }

  protected async save(): Promise<void> {
    await this.run(() => this.events.savePreSelection({ phaseId: this.phaseId(), lines: this.lines() }));
  }

  protected async release(): Promise<void> {
    await this.run(() => this.events.releasePreSelection(this.phaseId()));
  }

  private async apply(token: string): Promise<void> {
    const selection = await this.run(() => this.events.applyPreSelection(this.phaseId(), token));
    if (selection && selection.applied.length > 0) this.held.emit(selection);
  }

  private update(line: number, alternative: number, change: (a: SparkPreSelectLine['alternatives'][number]) => SparkPreSelectLine['alternatives'][number]): void {
    this.lines.update((lines) => lines.map((l, i) => i === line
      ? { alternatives: l.alternatives.map((a, j) => j === alternative ? change(a) : a) }
      : l));
  }

  private async run(action: () => Promise<SparkPreSelection>): Promise<SparkPreSelection | null> {
    this.busy.set(true);
    this.refusal.set(null);
    try {
      const selection = await action();
      this.selection.set(selection);
      return selection;
    } catch (e) {
      this.refusal.set(ticketingRefusalKey(ticketingRefusalOf(e)?.code ?? 'conflict'));
      return null;
    } finally {
      this.busy.set(false);
    }
  }
}
