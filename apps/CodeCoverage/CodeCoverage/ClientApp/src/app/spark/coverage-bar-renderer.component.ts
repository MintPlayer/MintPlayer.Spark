import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { PersistentObject } from '@mintplayer/ng-spark/models';
import type { SparkAttributeColumnRenderer } from '@mintplayer/ng-spark/renderers';
import { CoverageSummary } from '../services/browse.service';
import { CoverageBarComponent } from '../components/coverage-bar/coverage-bar.component';
import { toCoverageSummary } from './coverage-summary';

/**
 * Column slot of the "coverage-bar" renderer: the compact bar for grid cells.
 * The detail slot is CoverageSummaryDetailRendererComponent (ring + stats).
 */
@Component({
  selector: 'app-coverage-bar-renderer',
  imports: [CoverageBarComponent],
  template: `<app-coverage-bar [summary]="summary()" />`,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class CoverageBarRendererComponent implements SparkAttributeColumnRenderer {
  value = input<any>();
  options = input<Record<string, any> | undefined>();

  readonly summary = computed<CoverageSummary | null>(() => toCoverageSummary(this.value() as PersistentObject));
}
