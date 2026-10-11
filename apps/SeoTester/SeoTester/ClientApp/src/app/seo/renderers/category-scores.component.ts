import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkThresholdBarComponent, SparkThresholdBarOptions, sparkLetterGrades } from '@mintplayer/ng-spark/score-renderers';
import { SEO_CATEGORY_ORDER, SeoCatalogueService } from '../seo-catalogue.service';
import { asRows, num, str } from '../rows';

/** The bar of one category: the score renderers' letter grades, a whole number, no unit. Spelled out (score-renderers guide). */
export const SEO_CATEGORY_BAR: SparkThresholdBarOptions = {
  min: 0, max: 100, bands: sparkLetterGrades, decimals: 0, suffix: '', label: 'Category score', width: 'min(160px, 100%)', height: '8px', showValue: true,
};

/**
 * `seo-category-scores`, the full-width renderer of `AuditRun.CategoryScores` (PRD §7): a bar per category in the
 * catalogue's order, "not measured" for a category nothing was measured in, and a note when a failing critical check
 * capped it. Domain/DNS is information only (weight 0), and says so.
 */
@Component({
  selector: 'app-seo-category-scores',
  imports: [SparkThresholdBarComponent, TranslateKeyPipe],
  template: `
    @if (categories().length === 0) {
      <span class="text-muted">—</span>
    } @else {
      <div class="seo-cats">
          @for (c of categories(); track c.category) {
            <div class="seo-cat" [attr.data-category]="c.category">
              <span>{{ catalogue.categoryLabel(c.category) }}</span>
              <span class="seo-cat-value">
                @if (c.score === null) {
                  <span class="text-body-secondary">{{ 'seoUi.notMeasured' | t }}</span>
                } @else {
                  <spark-threshold-bar class="seo-cat-bar" [value]="c.score" [options]="barFor(c.category)" />
                }
                @if (c.capped) { <span class="small text-body-secondary" data-capped>{{ 'seoUi.capped' | t }}</span> }
                @if (c.category === 'Domain') { <span class="small text-body-secondary">{{ 'seoUi.notScored' | t }}</span> }
              </span>
            </div>
          }
      </div>
    }
  `,
  styles: [`
    /* Two columns: the label, then the bar with its notes, which wrap under the bar on a phone (375 px). */
    .seo-cats { display: grid; grid-template-columns: max-content minmax(0, 1fr); gap: .35rem 1rem; align-items: center; }
    .seo-cat { display: contents; }
    .seo-cat-value { display: flex; flex-wrap: wrap; align-items: center; gap: .25rem 1rem; min-width: 0; }
    .seo-cat-bar { flex: 0 1 13rem; min-width: 0; }
  `],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SeoCategoryScoresComponent {
  readonly value = input<unknown>();
  protected readonly catalogue = inject(SeoCatalogueService);

  protected readonly categories = computed(() => {
    const order = (c: string) => { const i = SEO_CATEGORY_ORDER.indexOf(c); return i < 0 ? 99 : i; };
    return asRows(this.value())
      .map(row => ({ category: str(row, 'Category') ?? '', score: num(row, 'Score'), capped: row['Capped'] === true }))
      .filter(c => c.category)
      .sort((a, b) => order(a.category) - order(b.category));
  });

  protected barFor(category: string): SparkThresholdBarOptions {
    return { ...SEO_CATEGORY_BAR, label: this.catalogue.categoryLabel(category) };
  }
}
