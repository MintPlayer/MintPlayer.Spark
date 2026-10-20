import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { formatMoney } from '@mintplayer/ng-spark/models';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { CommercePriceDisplay } from './generated/contracts';

/**
 * A price as the storefront must show it (commerce #521 M4, default 4 + GA3): the final price with VAT
 * and every mandatory fee, an optional fee breakdown, and — whenever the server sent one — the
 * Omnibus prior price (the lowest price of the 30 days before the reduction, PID Art. 6a). The prior
 * price is rendered whenever present; a page cannot show a reduction without it.
 */
@Component({
  selector: 'spark-commerce-price',
  imports: [TranslateKeyPipe],
  templateUrl: './spark-commerce-price.component.html',
  styleUrl: './spark-commerce-price.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkCommercePriceComponent {
  private readonly lang = inject(SparkLanguageService);

  /** The server's price display (`CommerceListingInfo.price` or a quote). */
  price = input<CommercePriceDisplay | null>(null);

  /** Whether to list the mandatory fees below the price (checkout shows them, listings may not). */
  breakdown = input(false);

  protected readonly total = computed(() => this.format(this.price()?.unitTotal));
  protected readonly prior = computed(() => {
    const prior = this.price()?.priorPrice;
    return prior ? this.lang.t('commerce.price.priorPrice').replace('{0}', formatMoney(prior)) : null;
  });
  protected readonly vat = computed(() => {
    const vat = this.price()?.vat;
    return vat ? this.lang.t('commerce.price.vatIncluded').replace('{0}', formatMoney(vat)) : null;
  });
  protected readonly fees = computed(() =>
    (this.price()?.mandatoryFees ?? []).map(f => ({ label: this.lang.t(f.labelKey), amount: formatMoney(f.amount) })));

  private format(value: unknown): string {
    return value ? formatMoney(value) : '';
  }
}
