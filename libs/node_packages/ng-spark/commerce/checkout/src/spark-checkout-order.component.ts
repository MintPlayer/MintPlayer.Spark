import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { formatMoney } from '@mintplayer/ng-spark/models';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { ResolveTranslationPipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkOrderView } from './generated/contracts';
import { refusalKey, refusalOf, SparkCommerceCheckoutService } from './spark-commerce-checkout.service';
import { SparkCheckoutAftercareComponent } from './spark-checkout-aftercare.component';
import { SparkCheckoutHoldTimerComponent } from './spark-checkout-hold-timer.component';

/**
 * The buyer's own order (commerce #521): its state, lines (with the ID-check marker, GB12), extras, total and any
 * refunded amount. While the order awaits payment it offers "Pay now" (the provider's page) and "Cancel the payment"
 * (D3: the holds go back at once). Shows "order not found" for an order that is not the caller's.
 *
 * `<spark-checkout-order [number]="orderNumber" />`
 */
@Component({
  selector: 'spark-checkout-order',
  imports: [TranslateKeyPipe, ResolveTranslationPipe, SparkCheckoutAftercareComponent, SparkCheckoutHoldTimerComponent],
  templateUrl: './spark-checkout-order.component.html',
  styleUrl: './spark-checkout-order.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkCheckoutOrderComponent {
  private readonly checkout = inject(SparkCommerceCheckoutService);
  private readonly lang = inject(SparkLanguageService);

  /** The order number. */
  number = input.required<string>();

  /** M9: a guest's signed order link token (`?access=` of the mailed link): the withdrawal function works without the original browser. */
  access = input<string | null>(null);

  protected readonly order = signal<SparkOrderView | null>(null);
  protected readonly loaded = signal(false);
  protected readonly busy = signal(false);
  protected readonly refusal = signal<string | null>(null);

  protected readonly title = computed(() => this.lang.t('commerceCheckout.orderNumber').replace('{0}', this.number()));
  protected readonly canPay = computed(() => {
    const order = this.order();
    return !!order && order.state === 'PaymentPending' && !!order.paymentUrl;
  });

  constructor() {
    effect(() => {
      const number = this.number();
      this.order.set(null);
      this.loaded.set(false);
      void this.checkout.order(number).then(
        (order) => { this.order.set(order); this.loaded.set(true); },
        (e) => { this.showRefusal(e); this.loaded.set(true); });
    });
  }

  protected stateKey(state: string): string {
    return 'commerceCheckout.state.' + state;
  }

  protected money(value: unknown): string {
    return value ? formatMoney(value) : '';
  }

  protected async cancelPayment(): Promise<void> {
    this.busy.set(true);
    this.refusal.set(null);
    try {
      this.order.set(await this.checkout.cancelPayment(this.number()));
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
