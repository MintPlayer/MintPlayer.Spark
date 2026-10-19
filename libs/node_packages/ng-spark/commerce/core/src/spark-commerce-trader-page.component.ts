import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { CommerceTraderPage } from './generated/contracts';
import { SparkCommerceService } from './spark-commerce.service';

/**
 * The public trader page (commerce #521 M4, GA5; DSA Art. 30(7)): the seller's name, address, contact
 * details, trade register and self-certification. Routable (`withComponentInputBinding()` binds
 * `:organizationId`) or embeddable.
 */
@Component({
  selector: 'spark-commerce-trader-page',
  imports: [TranslateKeyPipe],
  templateUrl: './spark-commerce-trader-page.component.html',
  styleUrl: './spark-commerce-trader-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkCommerceTraderPageComponent {
  private readonly commerce = inject(SparkCommerceService);

  /** The seller organization id. */
  organizationId = input<string | null>(null);

  protected readonly page = signal<CommerceTraderPage | null>(null);
  protected readonly loaded = signal(false);

  constructor() {
    effect(() => {
      const id = this.organizationId();
      this.loaded.set(false);
      if (!id) { this.page.set(null); return; }
      this.commerce.traderPage(id).then(page => {
        if (this.organizationId() !== id) return;
        this.page.set(page);
        this.loaded.set(true);
      });
    });
  }
}
