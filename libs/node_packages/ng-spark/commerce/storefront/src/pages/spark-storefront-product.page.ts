import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { Router } from '@angular/router';
import { resolveTranslation } from '@mintplayer/ng-spark/models';
import { SparkShopProduct, SparkShopProductComponent } from '@mintplayer/ng-spark/commerce/shop';
import { injectStorefront } from '../storefront-config';
import { storefrontUrl } from '../storefront-paths';
import { SparkStorefrontMeta } from '../storefront-meta';

/**
 * The product page (`/shop/products/{slug}`, `?variant=`): the server already put the title, description, OpenGraph
 * image and JSON-LD into `index.html` (X6/X22); this page sets the same tags client-side after navigation (S19).
 * "Add to basket" opens the basket.
 */
@Component({
  selector: 'spark-storefront-product-page',
  imports: [SparkShopProductComponent],
  template: `<spark-shop-product [slug]="slug()" [variant]="variant() ?? ''" (productLoaded)="setMeta($event)" (added)="toBasket()" />`,
  styleUrl: './spark-storefront-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkStorefrontProductPage {
  private readonly router = inject(Router);
  private readonly meta = inject(SparkStorefrontMeta);
  private readonly paths = injectStorefront().paths;

  /** The `:slug` route value. */
  slug = input.required<string>();

  /** The `?variant=` query value. */
  variant = input<string | null | undefined>(null);

  protected setMeta(product: SparkShopProduct | null): void {
    if (!product) return;
    const title = resolveTranslation(product.name) || product.slug;
    this.meta.set({
      title,
      description: (product.description ? resolveTranslation(product.description) : '') || title,
      image: product.media[0]?.url ?? null,
      type: 'product',
    });
  }

  protected toBasket(): void {
    void this.router.navigateByUrl(storefrontUrl(this.paths.checkout, 'basket'));
  }
}
