import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { formatMoney } from '@mintplayer/ng-spark/models';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { ResolveTranslationPipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkDateTimeComponent } from '@mintplayer/ng-spark/date-time';
import { SparkCommerceCheckoutService } from '@mintplayer/ng-spark/commerce/checkout';
import {
  SparkEventAvailability,
  SparkPublicDate,
  SparkPublicEvent,
  SparkPublicPhase,
  SparkPublicPrice,
  SparkPublicTicketType,
} from './generated/contracts';
import {
  SparkTicketingEventsService,
  TICKETING_PASS_DATE,
  ticketingRefusalKey,
  ticketingRefusalOf,
  ticketItemId,
} from './spark-ticketing-events.service';

/** A seated date's price was chosen: the host opens the seat picker (`ticketing/venues`) for it. */
export interface SparkTicketSeatRequest {
  date: SparkPublicDate;
  phase: SparkPublicPhase;
  price: SparkPublicPrice;
  /** The item id without seats; add the picked seats as `units` (or a `chain`/`block` option). */
  itemId: string;
}

/** The admission token a phase's line needs (its `admissionScope`), as checkout's `admissionTokens`; null when none. */
export function admissionTokensFor(phase: Pick<SparkPublicPhase, 'admissionScope'>, tokens: Record<string, string>): Record<string, string> | null {
  const scope = phase.admissionScope;
  return scope && tokens[scope] ? { [scope]: tokens[scope] } : null;
}

/** One sellable row of the selected date and phase. */
interface OfferRow {
  key: string;
  type: SparkPublicTicketType;
  price: SparkPublicPrice;
  itemId: string;
  seated: boolean;
}

/**
 * The public event page (commerce #521 M13, PRD §4.11): the event, its dates in the viewer's zone with the venue-local
 * hint (D33/D35, `<spark-date-time [alsoIn]>`), the open phases' ticket types at their FinalPrice (fees included,
 * §4.4), places left per date, a presale-code field, "add to calendar" (GB9) and "add to basket" through checkout.
 * A seated date emits {@link seatsRequested} so the host shows the seat picker. Add-ons (GB2) are added from the basket
 * with their parent line, so they are listed but not sold here.
 *
 * `<spark-ticketing-event [slug]="slug" (seatsRequested)="openSeatPicker($event)" (added)="goToBasket()" />`
 */
@Component({
  selector: 'spark-ticketing-event',
  imports: [TranslateKeyPipe, ResolveTranslationPipe, SparkDateTimeComponent],
  templateUrl: './spark-ticketing-event.component.html',
  styleUrl: './spark-ticketing-event.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkTicketingEventComponent {
  private readonly events = inject(SparkTicketingEventsService);
  private readonly checkout = inject(SparkCommerceCheckoutService);
  private readonly lang = inject(SparkLanguageService);

  /** The event's slug (the `/events/{slug}` route value). */
  slug = input.required<string>();

  /** A seated date's price was chosen. */
  seatsRequested = output<SparkTicketSeatRequest>();

  /** Tickets were put in the basket. */
  added = output<string>();

  /** A queued phase's "join the queue" was chosen (§4.12): the host opens its waiting room (storefront: `/queue/{key}`). */
  queueRequested = output<SparkPublicPhase>();

  /** The event was loaded (null: none published under the slug); the storefront sets the page's meta tags from it. */
  eventLoaded = output<SparkPublicEvent | null>();

  /**
   * The waiting room's admission tokens by scope (`ticketing-phase:{phaseId}`, §4.12); a queued phase's line is added
   * with its token. The storefront fills it from the waiting room page (single-use, kept for the tab only).
   */
  admissionTokens = input<Record<string, string>>({});

  protected readonly event = signal<SparkPublicEvent | null>(null);
  protected readonly loaded = signal(false);
  protected readonly availability = signal<SparkEventAvailability | null>(null);
  protected readonly selectedDateId = signal<string | null>(null);
  protected readonly presaleCode = signal('');
  protected readonly quantities = signal<Record<string, number>>({});
  protected readonly busy = signal(false);
  protected readonly refusal = signal<string | null>(null);
  protected readonly message = signal<string | null>(null);

  protected readonly selectedDate = computed(() => {
    const ev = this.event();
    return ev?.dates.find((d) => d.id === this.selectedDateId()) ?? ev?.dates[0] ?? null;
  });

  /** The phases selling the selected date (open or announced). */
  protected readonly phases = computed(() => {
    const date = this.selectedDate();
    return (this.event()?.phases ?? []).filter((p) => !!date && p.dateIds.includes(date.id));
  });

  protected readonly calendarUrl = computed(() => {
    const date = this.selectedDate();
    return date ? this.events.calendarUrl(date.id, this.lang.language()) : null;
  });

  constructor() {
    effect(() => {
      const slug = this.slug();
      this.event.set(null);
      this.loaded.set(false);
      void this.events.publicEvent(slug).then(
        async (ev) => {
          this.event.set(ev);
          this.selectedDateId.set(ev?.dates.find((d) => d.onSale)?.id ?? ev?.dates[0]?.id ?? null);
          this.loaded.set(true);
          this.eventLoaded.emit(ev);
          if (ev) this.availability.set(await this.events.availability(ev.id).catch(() => null));
        },
        () => this.loaded.set(true));
    });
  }

  /** The rows of a phase for the selected date: admissions and passes covering it, cheapest category first. */
  protected rows(phase: SparkPublicPhase): OfferRow[] {
    const ev = this.event();
    const date = this.selectedDate();
    if (!ev || !date) return [];
    return phase.prices
      .map((price) => ({ price, type: ev.ticketTypes.find((t) => t.id === price.ticketTypeId) }))
      .filter((row): row is { price: SparkPublicPrice; type: SparkPublicTicketType } => !!row.type)
      .filter((row) => row.type.kind !== 'Pass' || row.type.dateIds.includes(date.id))
      .filter((row) => row.type.kind === 'Pass' || row.type.dateIds.length === 0 || row.type.dateIds.includes(date.id))
      .map((row) => ({
        key: `${phase.id}|${row.type.id}|${row.price.category}`,
        type: row.type,
        price: row.price,
        itemId: ticketItemId(phase.id, row.type.id, row.price.category, row.type.kind === 'Pass' ? TICKETING_PASS_DATE : date.id),
        seated: !!date.layoutSnapshotId && row.type.kind !== 'Pass' && row.type.kind !== 'AddOn',
      }))
      .sort((a, b) => a.price.finalPrice.Amount - b.price.finalPrice.Amount);
  }

  protected placesLeft(dateId: string): number | null {
    return this.availability()?.dates.find((d) => d.dateId === dateId)?.available ?? null;
  }

  protected soldOut(dateId: string): { soldOut: boolean; mayComeBack: boolean } {
    const date = this.availability()?.dates.find((d) => d.dateId === dateId);
    return { soldOut: !!date?.soldOut, mayComeBack: !!date?.holdsOutstanding };
  }

  protected money(value: unknown): string {
    return value ? formatMoney(value) : '';
  }

  protected quantity(key: string): number {
    return this.quantities()[key] ?? 1;
  }

  protected setQuantity(key: string, value: string): void {
    const quantity = Math.max(1, Math.min(50, Number.parseInt(value, 10) || 1));
    this.quantities.update((q) => ({ ...q, [key]: quantity }));
  }

  protected selectDate(dateId: string): void {
    this.selectedDateId.set(dateId);
    this.message.set(null);
    this.refusal.set(null);
  }

  protected setPresaleCode(value: string): void {
    this.presaleCode.set(value);
  }

  protected async add(phase: SparkPublicPhase, row: OfferRow): Promise<void> {
    const date = this.selectedDate();
    if (!date) return;
    if (row.seated) {
      this.seatsRequested.emit({ date, phase, price: row.price, itemId: row.itemId });
      return;
    }
    this.busy.set(true);
    this.refusal.set(null);
    this.message.set(null);
    try {
      const code = this.presaleCode().trim();
      await this.checkout.add({
        itemId: row.itemId,
        quantity: this.quantity(row.key),
        options: phase.presaleCodeRequired && code ? { presale: code } : null,
        admissionTokens: admissionTokensFor(phase, this.admissionTokens()),
      });
      this.message.set('ticketingEvents.addToBasket');
      this.added.emit(row.itemId);
      const ev = this.event();
      if (ev) this.availability.set(await this.events.availability(ev.id).catch(() => this.availability()));
    } catch (e) {
      this.refusal.set(ticketingRefusalKey(ticketingRefusalOf(e)?.code ?? 'conflict'));
    } finally {
      this.busy.set(false);
    }
  }
}
