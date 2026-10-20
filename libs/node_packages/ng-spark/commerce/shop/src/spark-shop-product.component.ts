import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { formatMoney } from '@mintplayer/ng-spark/models';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { ResolveTranslationPipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkCommerceCheckoutService } from '@mintplayer/ng-spark/commerce/checkout';
import { SparkShopMedia, SparkShopProduct, SparkShopReviewPage, SparkShopVariant } from './generated/contracts';
import { shopRefusalKey, shopRefusalOf, SparkCommerceShopService } from './spark-commerce-shop.service';

/**
 * A product page (commerce #521 M12, PRD §4.10): the gallery of the chosen variant (GB13), the FinalPrice with the
 * computed unit price beside it (GA3, PID Art. 3), availability, "Add to basket", the minimum age (GB12), the product
 * safety block (GA7, GPSR Art. 19), the legal-guarantee notice and any durability guarantee (GA8), a withdrawal
 * exemption (CRD Art. 16), and the reviews with the verification statement (GC5, UCPD Annex I 23b).
 *
 * `<spark-shop-product [slug]="slug" />`
 */
@Component({
  selector: 'spark-shop-product',
  imports: [TranslateKeyPipe, ResolveTranslationPipe],
  templateUrl: './spark-shop-product.component.html',
  styleUrl: './spark-shop-product.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkShopProductComponent {
  private readonly shop = inject(SparkCommerceShopService);
  private readonly checkout = inject(SparkCommerceCheckoutService);
  private readonly lang = inject(SparkLanguageService);

  /** The product's slug. */
  slug = input.required<string>();

  /** The variant to preselect (`?variant=` of a feed or JSON-LD link). */
  variant = input<string>('');

  /** A line was added to the basket. */
  added = output<SparkShopVariant>();

  /** The product was loaded (null: none published under the slug); the storefront sets the page's meta tags from it. */
  productLoaded = output<SparkShopProduct | null>();

  protected readonly product = signal<SparkShopProduct | null>(null);
  protected readonly loaded = signal(false);
  protected readonly reviews = signal<SparkShopReviewPage | null>(null);
  protected readonly chosenId = signal<string | null>(null);
  protected readonly quantity = signal(1);
  protected readonly busy = signal(false);
  protected readonly message = signal<string | null>(null);
  protected readonly refusal = signal<string | null>(null);

  protected readonly chosen = computed<SparkShopVariant | null>(() => {
    const product = this.product();
    if (!product) return null;
    return product.variants.find((v) => v.id === this.chosenId()) ?? product.variants[0] ?? null;
  });

  /** GB13: the chosen variant's images first, then the images for every variant. */
  protected readonly gallery = computed<SparkShopMedia[]>(() => {
    const product = this.product();
    const variant = this.chosen();
    if (!product) return [];
    const own = variant ? product.media.filter((m) => m.variantIds.includes(variant.id)) : [];
    return [...own, ...product.media.filter((m) => m.variantIds.length === 0)];
  });

  /** The option names the variants differ in (size, colour). */
  protected readonly optionNames = computed(() => {
    const names = new Set<string>();
    for (const v of this.product()?.variants ?? []) for (const o of v.options) names.add(o.name);
    return [...names];
  });

  constructor() {
    effect(() => {
      const slug = this.slug();
      this.product.set(null);
      this.loaded.set(false);
      this.reviews.set(null);
      void this.shop.product(slug).then(async (product) => {
        this.product.set(product);
        this.chosenId.set(product?.variants.find((v) => v.id === this.variant())?.id ?? product?.variants[0]?.id ?? null);
        this.loaded.set(true);
        this.productLoaded.emit(product);
        if (product?.reviewsEnabled) this.reviews.set(await this.shop.reviews(product.id));
      }, () => this.loaded.set(true));
    });
  }

  protected money(value: unknown): string {
    return value ? formatMoney(value) : '';
  }

  /** GA3: "€ 12,40 per kg" (or "per 100 g" when the reference quantity is not 1). */
  protected unitPrice(variant: SparkShopVariant): string {
    const product = this.product();
    if (!variant.unitPrice || !product?.unitSymbol) return '';
    const unitKey = 'commerceShop.unit.' + product.unitSymbol;
    const unitText = this.lang.t(unitKey);
    const unit = (product.unitPriceReference && product.unitPriceReference !== 1 ? `${product.unitPriceReference} ` : '') + (unitText === unitKey ? product.unitSymbol : unitText);
    return this.lang.t('commerceShop.product.unitPrice').replace('{price}', formatMoney(variant.unitPrice)).replace('{unit}', unit);
  }

  protected availability(variant: SparkShopVariant): string {
    return this.lang.t('commerceShop.availability.' + variant.availability).replace('{count}', String(variant.available ?? ''));
  }

  protected optionLabel(variant: SparkShopVariant): string {
    return variant.options.map((o) => o.value).join(' · ') || variant.sku;
  }

  protected text(key: string, values: Record<string, string | number>): string {
    let text = this.lang.t(key);
    for (const [name, value] of Object.entries(values)) text = text.replace(`{${name}}`, String(value));
    return text;
  }

  protected stars(rating: number): string {
    return '★'.repeat(rating) + '☆'.repeat(Math.max(0, 5 - rating));
  }

  protected setQuantity(value: string): void {
    const parsed = Math.floor(Number(value));
    this.quantity.set(Number.isFinite(parsed) && parsed > 0 ? parsed : 1);
  }

  protected async addToBasket(): Promise<void> {
    const variant = this.chosen();
    if (!variant || variant.availability === 'OutOfStock') return;
    this.busy.set(true);
    this.message.set(null);
    this.refusal.set(null);
    try {
      await this.checkout.add({ itemId: variant.itemId, quantity: this.quantity() });
      this.message.set('commerceShop.product.added');
      this.added.emit(variant);
    } catch (e) {
      this.refusal.set(shopRefusalKey(shopRefusalOf(e)?.code ?? 'conflict', this.lang));
    } finally {
      this.busy.set(false);
    }
  }
}
