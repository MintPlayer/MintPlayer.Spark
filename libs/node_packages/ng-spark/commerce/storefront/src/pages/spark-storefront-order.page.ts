import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { SparkCheckoutOrderComponent } from '@mintplayer/ng-spark/commerce/checkout';
import { SparkOrderInvoicesComponent } from '@mintplayer/ng-spark/commerce/invoicing';
import { SparkShopOrderComponent } from '@mintplayer/ng-spark/commerce/shop';
import { SparkAttendeeAnswersComponent } from '@mintplayer/ng-spark/ticketing/events';
import { injectStorefront } from '../storefront-config';

/**
 * The buyer's order page (`/checkout/orders/{number}`, `?access=` for a guest's mailed link): Checkout's order with
 * the payment, the withdrawal function, cancellations and claims (GA1, GA8); the shop's deliveries and tracking; the
 * invoices and credit notes; the attendee questions of ticket lines (GB3). Each part renders nothing when it does not
 * apply, so one page serves the shop, ticketing and the festival.
 */
@Component({
  selector: 'spark-storefront-order-page',
  imports: [SparkCheckoutOrderComponent, SparkShopOrderComponent, SparkOrderInvoicesComponent, SparkAttendeeAnswersComponent],
  template: `
    <spark-checkout-order [number]="number()" [access]="access() ?? null" />
    @if (config.shop) {
      <spark-shop-order class="spark-storefront-page-section" [number]="number()" />
    }
    @if (!access()) {
      <spark-order-invoices class="spark-storefront-page-section" [orderNumber]="number()" />
    }
    @if (config.ticketing && !access()) {
      <spark-attendee-answers class="spark-storefront-page-section" [orderNumber]="number()" />
    }
  `,
  styleUrl: './spark-storefront-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkStorefrontOrderPage {
  protected readonly config = injectStorefront();

  /** The `:number` route value. */
  number = input.required<string>();

  /** The `?access=` token of a guest's order link. */
  access = input<string | null | undefined>(null);
}
