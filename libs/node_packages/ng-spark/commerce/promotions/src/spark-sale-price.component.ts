import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { formatMoney } from '@mintplayer/ng-spark/models';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SparkSalePriceDisplay } from './generated/contracts';
import { SparkCommercePromotionsService } from './spark-commerce-promotions.service';

/**
 * A listing price during a seller sale (GA3, Price Indication Directive 98/6/EC Art. 6a): the sale price, and — only
 * when the server says the reduction may be announced — the "was" price, the percentage computed on the prior price and
 * the prior price itself (the lowest final price of the 30 days before the reduction). Without a prior price the sale
 * price is shown as the plain price: no strike-through, no badge, no percentage.
 *
 * `<spark-sale-price [itemId]="product.id" />` or, with a display already loaded, `<spark-sale-price [display]="d" />`.
 */
@Component({
  selector: 'spark-sale-price',
  templateUrl: './spark-sale-price.component.html',
  styleUrl: './spark-sale-price.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkSalePriceComponent {
  private readonly promotions = inject(SparkCommercePromotionsService);
  private readonly lang = inject(SparkLanguageService);

  /** The item to load the display of. */
  itemId = input<string>('');

  /** A display the page already has (wins over `itemId`). */
  display = input<SparkSalePriceDisplay | null>(null);

  private readonly loaded = signal<SparkSalePriceDisplay | null>(null);

  protected readonly current = computed(() => this.display() ?? this.loaded());
  protected readonly announced = computed(() => {
    const d = this.current();
    return !!d && d.onSale && d.announceReduction;
  });
  protected readonly price = computed(() => this.format(this.current()?.finalPrice));
  protected readonly was = computed(() => (this.announced() ? this.format(this.current()?.regularFinalPrice) : ''));
  protected readonly percent = computed(() => {
    const value = this.announced() ? this.current()?.reductionPercent : null;
    return value ? this.lang.t('commercePromotions.reduction').replace('{0}', String(value)) : '';
  });
  protected readonly prior = computed(() => {
    const d = this.current();
    return this.announced() && d?.priorPrice ? this.lang.t('commercePromotions.priorPrice').replace('{0}', formatMoney(d.priorPrice)) : '';
  });

  private request = 0;

  constructor() {
    effect(() => {
      const id = this.itemId();
      if (this.display() || !id) return;
      const mine = ++this.request;
      this.promotions.salePrice(id).then(
        (d) => { if (mine === this.request) this.loaded.set(d); },
        () => { if (mine === this.request) this.loaded.set(null); },
      );
    });
  }

  private format(value: SparkSalePriceDisplay['finalPrice'] | null | undefined): string {
    return value ? formatMoney(value) : '';
  }
}
