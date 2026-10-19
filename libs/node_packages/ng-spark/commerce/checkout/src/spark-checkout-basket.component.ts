import { ChangeDetectionStrategy, Component, computed, DestroyRef, inject, OnInit, output, signal } from '@angular/core';
import { currentLanguage, formatMoney } from '@mintplayer/ng-spark/models';
import { formatSparkDateTime } from '@mintplayer/ng-spark/date-time';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { ResolveTranslationPipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkBasket, SparkBasketLine } from './generated/contracts';
import { refusalKey, refusalOf, SparkCommerceCheckoutService } from './spark-commerce-checkout.service';

/**
 * The basket (commerce #521): its lines with quantity controls, each line's hold ("Reserved until …" or expired), the
 * countdown to the earliest hold deadline (D40), the minimum-age note (GB12), the total, and the button that goes to
 * the last step. A basket locked by an order awaiting payment shows that order instead.
 *
 * `<spark-checkout-basket (checkout)="goToLastStep()" (resume)="openOrder($event)" />`
 */
@Component({
  selector: 'spark-checkout-basket',
  imports: [TranslateKeyPipe, ResolveTranslationPipe],
  templateUrl: './spark-checkout-basket.component.html',
  styleUrl: './spark-checkout-basket.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkCheckoutBasketComponent implements OnInit {
  private readonly checkout = inject(SparkCommerceCheckoutService);
  private readonly lang = inject(SparkLanguageService);

  /** The buyer goes to the last step. */
  readonly checkoutClicked = output<void>({ alias: 'checkout' });

  /** The buyer resumes the order awaiting payment (its number). */
  readonly resume = output<string>();

  protected readonly basket = signal<SparkBasket | null>(null);
  protected readonly busy = signal(false);
  protected readonly refusal = signal<string | null>(null);
  private readonly now = signal(Date.now());

  protected readonly lines = computed(() => this.basket()?.lines ?? []);
  protected readonly total = computed(() => this.money(this.basket()?.total));
  protected readonly minimumAge = computed(() => this.lines().reduce<number | null>(
    (max, line) => (line.minimumAge != null && (max == null || line.minimumAge > max) ? line.minimumAge : max), null));

  /** The time left on the earliest hold, `m:ss`; `null` without a hold or once it lapsed. */
  protected readonly countdown = computed(() => {
    const until = this.basket()?.earliestHoldUntil;
    if (!until) return null;
    const left = Math.floor((Date.parse(until) - this.now()) / 1000);
    if (!(left > 0)) return null;
    const minutes = Math.floor(left / 60);
    const seconds = left % 60;
    return this.lang.t('commerceCheckout.holdCountdown').replace('{0}', `${minutes}:${String(seconds).padStart(2, '0')}`);
  });

  constructor() {
    const timer = setInterval(() => this.now.set(Date.now()), 1000);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  ngOnInit(): void {
    void this.reload();
  }

  /** Loads the basket again. */
  async reload(): Promise<void> {
    try {
      this.basket.set(await this.checkout.basket());
    } catch (e) {
      this.showRefusal(e);
    }
  }

  protected money(value: unknown): string {
    return value ? formatMoney(value) : '';
  }

  /** "Reserved until …" in the viewer's zone (K10: the core facility converts, never this component). */
  protected heldUntilText(line: SparkBasketLine): string {
    return this.lang.t('commerceCheckout.heldUntil')
      .replace('{0}', formatSparkDateTime(line.heldUntil, 'viewer', 'time', currentLanguage()));
  }

  protected minimumAgeText(age: number): string {
    return this.lang.t('commerceCheckout.minimumAge').replace('{0}', String(age));
  }

  protected pendingText(number: string): string {
    return this.lang.t('commerceCheckout.orderNumber').replace('{0}', number);
  }

  protected setQuantity(line: SparkBasketLine, quantity: number): Promise<void> {
    return this.run(() => this.checkout.setQuantity(line.lineId, Math.max(0, quantity)));
  }

  protected goToCheckout(): void {
    this.checkoutClicked.emit();
  }

  private async run(action: () => Promise<SparkBasket>): Promise<void> {
    this.busy.set(true);
    this.refusal.set(null);
    try {
      this.basket.set(await action());
    } catch (e) {
      this.showRefusal(e);
    } finally {
      this.busy.set(false);
    }
  }

  private showRefusal(error: unknown): void {
    const refusal = refusalOf(error);
    this.refusal.set(refusalKey(refusal?.code ?? 'conflict', this.lang));
  }
}
