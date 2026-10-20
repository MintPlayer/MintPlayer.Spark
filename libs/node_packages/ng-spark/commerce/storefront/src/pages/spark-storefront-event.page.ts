import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { Router } from '@angular/router';
import { resolveTranslation } from '@mintplayer/ng-spark/models';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { refusalKey, refusalOf, SparkCommerceCheckoutService } from '@mintplayer/ng-spark/commerce/checkout';
import {
  admissionTokensFor, SparkPublicEvent, SparkPublicPhase, SparkTicketingEventComponent, SparkTicketSeatRequest,
} from '@mintplayer/ng-spark/ticketing/events';
import { SparkBestAvailableRequest, SparkSeatPickerComponent } from '@mintplayer/ng-spark/ticketing/venues';
import { injectStorefront } from '../storefront-config';
import { phaseRoomKey, storefrontUrl } from '../storefront-paths';
import { SparkStorefrontAdmissions } from '../storefront-admissions';
import { SparkStorefrontMeta } from '../storefront-meta';

/**
 * The event page (`/events/{slug}`): the event with its dates, phases and prices (M13); a seated date opens the seat
 * picker with its accessible list alternative (M14) right below; a queued phase sends the buyer to its waiting room and
 * the admission token comes back with them (§4.12). Client-side meta tags as on the product page (S19).
 */
@Component({
  selector: 'spark-storefront-event-page',
  imports: [TranslateKeyPipe, SparkTicketingEventComponent, SparkSeatPickerComponent],
  templateUrl: './spark-storefront-event.page.html',
  styleUrl: './spark-storefront-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkStorefrontEventPage {
  private readonly router = inject(Router);
  private readonly checkout = inject(SparkCommerceCheckoutService);
  private readonly lang = inject(SparkLanguageService);
  private readonly meta = inject(SparkStorefrontMeta);
  protected readonly admissions = inject(SparkStorefrontAdmissions);
  private readonly config = injectStorefront();

  /** The `:slug` route value. */
  slug = input.required<string>();

  /** The seated price being picked, with the seats chosen so far. */
  protected readonly seating = signal<SparkTicketSeatRequest | null>(null);
  protected readonly seats = signal<string[]>([]);
  protected readonly busy = signal(false);
  protected readonly refusal = signal<string | null>(null);

  protected setMeta(event: SparkPublicEvent | null): void {
    if (!event) return;
    const title = resolveTranslation(event.name) || event.slug;
    this.meta.set({ title, description: (event.description ? resolveTranslation(event.description) : '') || title, image: event.imageUrl ?? null });
  }

  protected openSeats(request: SparkTicketSeatRequest): void {
    this.refusal.set(null);
    this.seats.set([]);
    this.seating.set(request);
  }

  protected closeSeats(): void {
    this.seating.set(null);
  }

  /** The chosen seats as units of the seated price's item. */
  protected addSeats(): Promise<void> {
    const request = this.seating();
    const units = this.seats();
    if (!request || units.length === 0) return Promise.resolve();
    return this.add(request, { itemId: request.itemId, quantity: units.length, units });
  }

  /** The list alternative: block and quantity, best available (chain fill picks the seats). */
  protected addBestAvailable(best: SparkBestAvailableRequest): Promise<void> {
    const request = this.seating();
    if (!request) return Promise.resolve();
    const options: Record<string, string> = { block: best.blockKey };
    if (best.chainKey) options['chain'] = best.chainKey;
    return this.add(request, { itemId: request.itemId, quantity: best.quantity, options });
  }

  /** "Join the queue": the phase's automatic room, coming back here with the token. */
  protected async joinQueue(phase: SparkPublicPhase): Promise<void> {
    if (!this.config.waitingRoom) return;
    const key = await phaseRoomKey(phase.id);
    await this.router.navigate(['/', this.config.paths.queue, key], {
      queryParams: { scope: phase.admissionScope ?? null, return: storefrontUrl(this.config.paths.events, this.slug()) },
    });
  }

  protected toBasket(): void {
    void this.router.navigateByUrl(storefrontUrl(this.config.paths.checkout, 'basket'));
  }

  private async add(request: SparkTicketSeatRequest, line: { itemId: string; quantity: number; units?: string[]; options?: Record<string, string> }): Promise<void> {
    this.busy.set(true);
    this.refusal.set(null);
    try {
      await this.checkout.add({ ...line, admissionTokens: admissionTokensFor(request.phase, this.admissions.tokens()) });
      this.seating.set(null);
      this.toBasket();
    } catch (e) {
      this.refusal.set(refusalKey(refusalOf(e)?.code ?? 'conflict', this.lang));
    } finally {
      this.busy.set(false);
    }
  }
}
