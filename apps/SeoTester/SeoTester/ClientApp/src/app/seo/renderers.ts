import type { SparkAttributeRendererRegistration } from '@mintplayer/ng-spark/renderers';
import { SparkGaugeComponent } from '@mintplayer/ng-spark/score-renderers';
import { SeoScoreCellComponent } from './renderers/score-cell.component';
import { SeoCategoryScoresComponent } from './renderers/category-scores.component';
import { SeoCheckResultsComponent } from './renderers/check-results.component';
import { SeoPageFindingsComponent } from './renderers/page-findings.component';
import { SeoPreviewsComponent } from './renderers/previews.component';
import { SeoRunProgressComponent } from './renderers/run-progress.component';
import { SeoVerificationComponent } from './renderers/verification.component';

/**
 * SeoTester's attribute renderers, by the `renderer` names its App_Data model uses, on top of the score renderers
 * (`spark-gauge`, `spark-threshold-bar`, `spark-trend`, `spark-delta`; `provideSparkScoreRenderers` in app.config.ts).
 * The full-width ones are a whole section of a page (no label column); each also draws the share page's cell, which
 * goes through `spark-grid-cell`.
 */
export const seoRenderers: SparkAttributeRendererRegistration[] = [
  { name: 'seo-score', detailComponent: SparkGaugeComponent, columnComponent: SeoScoreCellComponent },
  { name: 'seo-run-progress', detailComponent: SeoRunProgressComponent, fullWidth: true },
  { name: 'seo-category-scores', detailComponent: SeoCategoryScoresComponent, columnComponent: SeoCategoryScoresComponent, fullWidth: true },
  { name: 'seo-check-results', detailComponent: SeoCheckResultsComponent, columnComponent: SeoCheckResultsComponent, fullWidth: true },
  { name: 'seo-verification', detailComponent: SeoVerificationComponent, fullWidth: true },
  { name: 'seo-page-findings', detailComponent: SeoPageFindingsComponent, columnComponent: SeoPageFindingsComponent, fullWidth: true },
  { name: 'seo-previews', detailComponent: SeoPreviewsComponent, fullWidth: true },
];
