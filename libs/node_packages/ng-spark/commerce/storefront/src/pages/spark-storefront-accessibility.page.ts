import { ChangeDetectionStrategy, Component, effect, inject, signal } from '@angular/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkCommerceLegalDocumentsComponent, SparkCommerceService } from '@mintplayer/ng-spark/commerce/core';
import { SPARK_LEGAL_KINDS } from '../storefront-paths';

/**
 * The accessibility statement (`/legal/accessibility`, GA10): the platform's published `accessibility-statement`
 * document (host text from `SPARK_ACCESSIBILITY_STATEMENT_TEMPLATE`, which explains the hold timer's "More time" and the
 * real-time exception beyond `Hold:Max`). Without one, a short notice says so; the template itself is never shown.
 */
@Component({
  selector: 'spark-storefront-accessibility-page',
  imports: [TranslateKeyPipe, SparkCommerceLegalDocumentsComponent],
  template: `
    <h1>{{ 'commerce.legal.kind.accessibility-statement' | t }}</h1>
    @if (available()) {
      <spark-commerce-legal-documents [kinds]="kinds" />
    } @else if (loaded()) {
      <p class="spark-storefront-page-muted">{{ 'commerceStorefront.legal.notPublished' | t }}</p>
    }
  `,
  styleUrl: './spark-storefront-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkStorefrontAccessibilityPage {
  private readonly commerce = inject(SparkCommerceService);
  protected readonly kinds = [SPARK_LEGAL_KINDS.accessibility];
  protected readonly loaded = signal(false);
  protected readonly available = signal(false);

  constructor() {
    effect(() => {
      void this.commerce.legalDocuments().then(
        (docs) => { this.available.set(docs.some((d) => d.kind === SPARK_LEGAL_KINDS.accessibility)); this.loaded.set(true); },
        () => this.loaded.set(true));
    });
  }
}
