import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { formatMoney } from '@mintplayer/ng-spark/models';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { ResolveTranslationPipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkShopCatalogItem, SparkShopCatalogPage, SparkShopPriceRange } from './generated/contracts';
import { SparkCommerceShopService, SparkShopCatalogSearch } from './spark-commerce-shop.service';

type CatalogSort = NonNullable<SparkShopCatalogSearch['sort']>;

/**
 * The shop catalogue (commerce #521 M12, GB13): typo-tolerant search, facet filters with counts (terms of one key widen,
 * keys narrow), price ranges, a sort, and "Show more". The ranking note says how results are ordered (GA5; no paid
 * placement). Each product links to `productPath` with its slug.
 *
 * `<spark-shop-catalog [category]="categoryId" productPath="/shop/products/{slug}" />`
 */
@Component({
  selector: 'spark-shop-catalog',
  imports: [TranslateKeyPipe, ResolveTranslationPipe],
  templateUrl: './spark-shop-catalog.component.html',
  styleUrl: './spark-shop-catalog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkShopCatalogComponent {
  private readonly shop = inject(SparkCommerceShopService);
  private readonly lang = inject(SparkLanguageService);

  /** A category id: its products and its subcategories'. */
  category = input<string>('');

  /** One seller's products. */
  seller = input<string>('');

  /** The product page route with `{slug}`. */
  productPath = input<string>('/shop/products/{slug}');

  /** Products per page. */
  pageSize = input<number>(24);

  /** A product was opened (for hosts that route themselves). */
  opened = output<SparkShopCatalogItem>();

  protected readonly words = signal('');
  protected readonly facets = signal<string[]>([]);
  protected readonly price = signal<number | null>(null);
  protected readonly sort = signal<CatalogSort>('');
  protected readonly page = signal<SparkShopCatalogPage | null>(null);
  protected readonly items = signal<SparkShopCatalogItem[]>([]);
  protected readonly loading = signal(false);
  protected readonly failed = signal(false);
  protected readonly sorts: CatalogSort[] = ['relevance', 'newest', 'price-asc', 'price-desc', 'rating'];
  protected readonly canLoadMore = computed(() => (this.page()?.total ?? 0) > this.items().length);
  private request = 0;

  constructor() {
    effect(() => {
      // Any filter change starts again at the first page.
      const search = this.search(0);
      void this.load(search, false);
    });
  }

  private search(skip: number): SparkShopCatalogSearch {
    return {
      q: this.words(),
      category: this.category(),
      seller: this.seller(),
      facets: this.facets(),
      price: this.price(),
      sort: this.sort(),
      skip,
      take: this.pageSize(),
    };
  }

  private async load(search: SparkShopCatalogSearch, append: boolean): Promise<void> {
    const id = ++this.request;
    this.loading.set(true);
    this.failed.set(false);
    try {
      const page = await this.shop.catalog(search);
      if (id !== this.request) return;
      this.page.set(page);
      this.items.set(append ? [...this.items(), ...page.items] : page.items);
    } catch {
      if (id === this.request) this.failed.set(true);
    } finally {
      if (id === this.request) this.loading.set(false);
    }
  }

  protected onSearch(value: string): void {
    this.words.set(value);
  }

  protected toggleFacet(term: string): void {
    const current = this.facets();
    this.facets.set(current.includes(term) ? current.filter((t) => t !== term) : [...current, term]);
  }

  protected setPrice(range: SparkShopPriceRange): void {
    this.price.set(this.price() === range.index ? null : range.index);
  }

  protected setSort(value: string): void {
    this.sort.set(value as CatalogSort);
  }

  protected loadMore(): void {
    void this.load(this.search(this.items().length), true);
  }

  protected link(item: SparkShopCatalogItem): string {
    return this.productPath().replace('{slug}', encodeURIComponent(item.slug));
  }

  protected facetLabel(key: string): string {
    const translationKey = 'commerceShop.facet.' + key;
    const text = this.lang.t(translationKey);
    return text === translationKey ? key : text;
  }

  protected rangeLabel(range: SparkShopPriceRange): string {
    const from = range.from ? formatMoney(range.from) : '';
    const to = range.to ? formatMoney(range.to) : '';
    return from && to ? `${from} – ${to}` : from ? `≥ ${from}` : `< ${to}`;
  }

  protected priceFrom(item: SparkShopCatalogItem): string {
    if (!item.priceFrom) return '';
    const money = formatMoney(item.priceFrom);
    return item.variesInPrice ? this.lang.t('commerceShop.catalog.priceFrom').replace('{price}', money) : money;
  }

  protected resultsText(): string {
    return this.lang.t('commerceShop.catalog.results').replace('{count}', String(this.page()?.total ?? 0));
  }
}
