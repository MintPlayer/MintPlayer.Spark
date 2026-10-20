import { ChangeDetectionStrategy, Component, inject, input, OnInit, signal } from '@angular/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkCommercePromotionsService } from '@mintplayer/ng-spark/commerce/promotions';

/** What the back-in-stock link does (route data `action`). */
export type SparkBackInStockAction = 'confirm' | 'unsubscribe';

/**
 * The back-in-stock mail links (GB11; Promotions' `ConfirmPagePath` and `UnsubscribePagePath`,
 * `/promotions/back-in-stock/{confirm|unsubscribe}?id=&token=`): the double opt-in confirmation, or ending the alert in
 * one step without signing in. The token travels in the POST body only.
 */
@Component({
  selector: 'spark-storefront-back-in-stock-page',
  imports: [TranslateKeyPipe],
  template: `
    <h1>{{ 'commerceStorefront.account.backInStock' | t }}</h1>
    @switch (state()) {
      @case ('busy') { <p role="status">{{ 'commerceStorefront.backInStock.busy' | t }}</p> }
      @case ('done') { <p role="status">{{ ('commerceStorefront.backInStock.' + action()) | t }}</p> }
      @case ('failed') { <p class="spark-storefront-page-refusal" role="alert">{{ 'commerceStorefront.backInStock.invalid' | t }}</p> }
    }
  `,
  styleUrl: './spark-storefront-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkStorefrontBackInStockPage implements OnInit {
  private readonly promotions = inject(SparkCommercePromotionsService);

  /** Route data: confirm or unsubscribe. */
  action = input<SparkBackInStockAction>('confirm');

  /** `?id=`. */
  id = input<string | null | undefined>(null);

  /** `?token=`. */
  token = input<string | null | undefined>(null);

  protected readonly state = signal<'busy' | 'done' | 'failed'>('busy');

  async ngOnInit(): Promise<void> {
    const id = this.id();
    const token = this.token();
    if (!id || !token) {
      this.state.set('failed');
      return;
    }
    try {
      if (this.action() === 'unsubscribe') await this.promotions.unsubscribeBackInStock(id, token);
      else await this.promotions.confirmBackInStock(id, token);
      this.state.set('done');
    } catch {
      this.state.set('failed');
    }
  }
}
