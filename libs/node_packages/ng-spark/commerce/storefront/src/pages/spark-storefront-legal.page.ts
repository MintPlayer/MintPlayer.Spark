import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkCommerceLegalDocumentsComponent, SparkCommerceService } from '@mintplayer/ng-spark/commerce/core';
import { injectStorefront } from '../storefront-config';
import { SPARK_LEGAL_KINDS, storefrontUrl } from '../storefront-paths';

/**
 * A legal page (GA2): the platform's document in force of one kind (route data `kind`: terms of sale, privacy policy,
 * withdrawal information with the model withdrawal form, cookie policy), with its version, effective date and PDF.
 * The withdrawal page also points to the withdrawal function on the buyer's orders (GA1: withdrawing needs no form).
 * A seller's own documents are at `/commerce/legal/{organizationId}`.
 */
@Component({
  selector: 'spark-storefront-legal-page',
  imports: [TranslateKeyPipe, SparkCommerceLegalDocumentsComponent],
  template: `
    <h1>{{ ('commerce.legal.kind.' + kind()) | t }}</h1>
    @if (available()) {
      <spark-commerce-legal-documents [kinds]="[kind()]" />
    } @else if (loaded()) {
      <p class="spark-storefront-page-muted">{{ 'commerceStorefront.legal.notPublished' | t }}</p>
    }
    @if (isWithdrawal()) {
      <section class="spark-storefront-page-section" aria-labelledby="spark-storefront-withdraw-title">
        <h2 id="spark-storefront-withdraw-title">{{ 'commerceStorefront.legal.withdrawFunction' | t }}</h2>
        <p>{{ 'commerceStorefront.legal.withdrawFunctionText' | t }}</p>
        <a class="btn btn-outline-primary" [href]="accountUrl">{{ 'commerceStorefront.nav.account' | t }}</a>
      </section>
    }
  `,
  styleUrl: './spark-storefront-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkStorefrontLegalPage {
  private readonly commerce = inject(SparkCommerceService);
  protected readonly accountUrl = storefrontUrl(injectStorefront().paths.account);

  /** The document kind (route data). */
  kind = input<string>(SPARK_LEGAL_KINDS.terms);

  protected readonly loaded = signal(false);
  protected readonly available = signal(false);
  protected readonly isWithdrawal = computed(() => this.kind() === SPARK_LEGAL_KINDS.withdrawal);

  constructor() {
    effect(() => {
      const kind = this.kind();
      this.loaded.set(false);
      void this.commerce.legalDocuments().then(
        (docs) => { this.available.set(docs.some((d) => d.kind === kind)); this.loaded.set(true); },
        () => this.loaded.set(true));
    });
  }
}
