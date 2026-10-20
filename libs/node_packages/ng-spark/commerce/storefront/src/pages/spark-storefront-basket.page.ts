import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { Router } from '@angular/router';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkCheckoutBasketComponent } from '@mintplayer/ng-spark/commerce/checkout';
import { injectStorefront } from '../storefront-config';
import { storefrontUrl } from '../storefront-paths';

/** The basket page (`/checkout/basket`): the basket with its hold timer (GA10); "Checkout" opens the last step. */
@Component({
  selector: 'spark-storefront-basket-page',
  imports: [TranslateKeyPipe, SparkCheckoutBasketComponent],
  template: `
    <h1 class="visually-hidden">{{ 'commerceCheckout.basket' | t }}</h1>
    <spark-checkout-basket (checkout)="checkout()" (resume)="resume($event)" />
  `,
  styleUrl: './spark-storefront-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkStorefrontBasketPage {
  private readonly router = inject(Router);
  private readonly paths = injectStorefront().paths;

  protected checkout(): void {
    void this.router.navigateByUrl(storefrontUrl(this.paths.checkout));
  }

  protected resume(number: string): void {
    void this.router.navigateByUrl(storefrontUrl(this.paths.checkout, 'orders', number));
  }
}
