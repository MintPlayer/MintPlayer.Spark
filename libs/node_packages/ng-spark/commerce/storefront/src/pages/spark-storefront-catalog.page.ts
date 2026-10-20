import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { ResolveTranslationPipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkCommerceShopService, SparkShopCatalogComponent, SparkShopCategory } from '@mintplayer/ng-spark/commerce/shop';
import { injectStorefront } from '../storefront-config';

/**
 * The catalogue (`/shop`) and a category (`/shop/categories/{slug}`, the server's `CategoryPagePath`): search, facets,
 * sort, pages (GB13). The route carries the category's slug; the catalogue filters by its id.
 */
@Component({
  selector: 'spark-storefront-catalog-page',
  imports: [TranslateKeyPipe, ResolveTranslationPipe, SparkShopCatalogComponent],
  template: `
    @if (category(); as c) {
      <h1>{{ c.name | resolveTranslation: c.slug }}</h1>
    } @else {
      <h1>{{ 'commerceStorefront.nav.shop' | t }}</h1>
    }
    @if (!slug() || category()) {
      <spark-shop-catalog [category]="category()?.id ?? ''" [productPath]="productPath" />
    } @else if (loaded()) {
      <p class="spark-storefront-page-refusal" role="alert">{{ 'commerceStorefront.notFound' | t }}</p>
    }
  `,
  styleUrl: './spark-storefront-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkStorefrontCatalogPage {
  private readonly shop = inject(SparkCommerceShopService);
  protected readonly productPath = `/${injectStorefront().paths.shop}/products/{slug}`;

  /** The category's `:slug` route value; empty on the catalogue itself. */
  slug = input<string | null | undefined>(null);

  private readonly categories = signal<SparkShopCategory[]>([]);
  protected readonly loaded = signal(false);
  protected readonly category = computed(() => {
    const slug = this.slug();
    return slug ? this.categories().find((c) => c.slug === slug) ?? null : null;
  });

  constructor() {
    effect(() => {
      if (!this.slug() || this.loaded()) return;
      void this.shop.categories().then(
        (list) => { this.categories.set(list); this.loaded.set(true); },
        () => this.loaded.set(true));
    });
  }
}
