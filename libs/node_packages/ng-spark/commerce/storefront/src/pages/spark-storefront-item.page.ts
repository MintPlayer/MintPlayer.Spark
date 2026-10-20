import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { Router } from '@angular/router';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkCommerceShopService } from '@mintplayer/ng-spark/commerce/shop';
import { injectStorefront } from '../storefront-config';
import { storefrontUrl } from '../storefront-paths';

/**
 * `/shop/item?id={itemId}` (Promotions' `ItemPagePath`, the back-in-stock mail's link): looks the checkout item up and
 * replaces itself with the real product page, `/shop/products/{slug}?variant=`, so the mail never needs the slug.
 */
@Component({
  selector: 'spark-storefront-item-page',
  imports: [TranslateKeyPipe],
  template: `
    @if (missing()) {
      <h1>{{ 'commerceStorefront.nav.shop' | t }}</h1>
      <p class="spark-storefront-page-refusal" role="alert">{{ 'commerceStorefront.notFound' | t }}</p>
    }
  `,
  styleUrl: './spark-storefront-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkStorefrontItemPage {
  private readonly shop = inject(SparkCommerceShopService);
  private readonly router = inject(Router);
  private readonly paths = injectStorefront().paths;

  /** `?id=`: the checkout item id. */
  id = input<string | null | undefined>(null);

  protected readonly missing = signal(false);

  constructor() {
    effect(() => {
      const id = this.id();
      this.missing.set(false);
      if (!id) {
        this.missing.set(true);
        return;
      }
      void this.shop.itemLink(id).then((link) => {
        if (!link) {
          this.missing.set(true);
          return;
        }
        void this.router.navigateByUrl(`${storefrontUrl(this.paths.shop, 'products', link.slug)}?variant=${encodeURIComponent(link.variantId)}`, { replaceUrl: true });
      }, () => this.missing.set(true));
    });
  }
}
