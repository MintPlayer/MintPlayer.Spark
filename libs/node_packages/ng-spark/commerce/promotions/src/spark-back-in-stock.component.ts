import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { promotionRefusalKey, promotionRefusalOf, SparkCommercePromotionsService } from './spark-commerce-promotions.service';

/**
 * "Notify me when it is back in stock" (GB11) for a sold-out variant. A signed-in buyer subscribes with the account's
 * address; a guest types an address and confirms it from the mail (double opt-in). Nothing is reserved: the first to
 * pay gets it.
 *
 * `<spark-back-in-stock [itemId]="variant.id" [signedIn]="auth.isSignedIn()" />`
 */
@Component({
  selector: 'spark-back-in-stock',
  imports: [TranslateKeyPipe],
  templateUrl: './spark-back-in-stock.component.html',
  styleUrl: './spark-back-in-stock.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkBackInStockComponent {
  private readonly promotions = inject(SparkCommercePromotionsService);

  /** The sold-out variant. */
  itemId = input.required<string>();

  /** Whether the buyer is signed in (then no address is asked). */
  signedIn = input(false);

  protected readonly email = signal('');
  protected readonly busy = signal(false);
  protected readonly state = signal<'idle' | 'subscribed' | 'confirm' | 'error'>('idle');
  protected readonly errorKey = signal('');

  protected setEmail(event: Event): void {
    this.email.set((event.target as HTMLInputElement).value);
  }

  protected async subscribe(event?: Event): Promise<void> {
    event?.preventDefault();
    if (this.busy()) return;
    this.busy.set(true);
    try {
      const result = await this.promotions.subscribeBackInStock({
        itemId: this.itemId(),
        email: this.signedIn() ? null : this.email().trim(),
      });
      this.state.set(result.confirmed ? 'subscribed' : 'confirm');
    } catch (error) {
      const refusal = promotionRefusalOf(error);
      this.errorKey.set(refusal ? promotionRefusalKey(refusal.code) : 'commercePromotions.refusal.promotionInvalidRequest');
      this.state.set('error');
    } finally {
      this.busy.set(false);
    }
  }
}
