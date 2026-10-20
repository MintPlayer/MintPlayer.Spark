import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { CommerceSellerLabel } from './generated/contracts';

/**
 * The seller label every listing shows (commerce #521 M4, GA5; CRD Art. 6a(1)(b)–(c)): whether the seller is
 * a trader — for a private seller, that EU consumer-protection rights do not apply — linked to the
 * public trader page (`traderPageRoute` + organization id).
 */
@Component({
  selector: 'spark-commerce-trader-label',
  imports: [TranslateKeyPipe, RouterLink],
  templateUrl: './spark-commerce-trader-label.component.html',
  styleUrl: './spark-commerce-trader-label.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkCommerceTraderLabelComponent {
  /** The seller label (`CommerceListingInfo.seller`). */
  seller = input<CommerceSellerLabel | null>(null);

  /** The route of the trader page; the organization id is appended. */
  traderPageRoute = input<string>('/commerce/traders');

  protected readonly statusKey = computed(() => `commerce.trader.${this.seller()?.traderStatus ?? 'Undeclared'}`);
  protected readonly link = computed(() => {
    const id = this.seller()?.organizationId;
    return id ? [this.traderPageRoute(), id] : null;
  });
  protected readonly nonTrader = computed(() => this.seller()?.traderStatus === 'NonTrader');
}
