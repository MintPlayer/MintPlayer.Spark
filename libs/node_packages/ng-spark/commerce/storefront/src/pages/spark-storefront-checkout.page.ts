import { ChangeDetectionStrategy, Component, inject, viewChild } from '@angular/core';
import { Router } from '@angular/router';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkCheckoutStepComponent, SparkPlaceOrderResult } from '@mintplayer/ng-spark/commerce/checkout';
import { SparkShopDeliveryStepComponent } from '@mintplayer/ng-spark/commerce/shop';
import { injectStorefront } from '../storefront-config';
import { inAppPath, isInAppUrl, storefrontUrl } from '../storefront-paths';

/**
 * The checkout page (`/checkout`): with the shop, the delivery step first (it hides itself when the basket holds no
 * goods), then Checkout's last step (GA2). A placed order without a provider redirect opens its order page.
 */
@Component({
  selector: 'spark-storefront-checkout-page',
  imports: [TranslateKeyPipe, SparkCheckoutStepComponent, SparkShopDeliveryStepComponent],
  template: `
    <h1>{{ 'commerceCheckout.checkout' | t }}</h1>
    @if (config.shop) {
      <spark-shop-delivery-step class="spark-storefront-page-section" (chosen)="deliveryChosen()" />
    }
    <spark-checkout-step class="spark-storefront-page-section" [traderPageRoute]="traderRoute" (placed)="placed($event)" />
  `,
  styleUrl: './spark-storefront-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkStorefrontCheckoutPage {
  private readonly router = inject(Router);
  protected readonly config = injectStorefront();
  protected readonly traderRoute = storefrontUrl(this.config.paths.commerce, 'traders');
  private readonly step = viewChild(SparkCheckoutStepComponent);

  /** The delivery choice priced a new extra: the last step reloads its summary. */
  protected deliveryChosen(): void {
    void this.step()?.load();
  }

  protected placed(result: SparkPlaceOrderResult): void {
    const origin = typeof location === 'undefined' ? 'http://localhost' : location.origin;
    if (result.redirectUrl && isInAppUrl(result.redirectUrl, origin)) {
      void this.router.navigateByUrl(inAppPath(result.redirectUrl, origin));
      return;
    }
    void this.router.navigateByUrl(storefrontUrl(this.config.paths.checkout, 'orders', result.number));
  }
}
