import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { PersistentObject } from '@mintplayer/ng-spark/models';
import type { SparkAttributeDetailRenderer } from '@mintplayer/ng-spark/renderers';
import { CoverageSummary } from '../services/browse.service';
import { CoverageBarComponent } from '../components/coverage-bar/coverage-bar.component';
import { toCoverageSummary } from './coverage-summary';

/**
 * Detail slot of the "coverage-bar" renderer: the bar plus the line/branch/file
 * counts the commit page used to draw by hand. The column slot stays the
 * compact bar (CoverageBarRendererComponent).
 */
@Component({
  selector: 'app-coverage-summary-detail-renderer',
  imports: [CoverageBarComponent],
  template: `
    @if (summary(); as s) {
      <div>
        <app-coverage-bar [summary]="s" />
        <div class="small text-muted">
          {{ s.linesCovered }}/{{ s.linesCoverable }} lines · {{ s.branchesCovered }}/{{ s.branchesTotal }} branches · {{ s.filesCount }} files
        </div>
      </div>
    } @else {
      <span class="text-muted">—</span>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class CoverageSummaryDetailRendererComponent implements SparkAttributeDetailRenderer {
  value = input<any>();
  options = input<Record<string, any> | undefined>();

  readonly summary = computed<CoverageSummary | null>(() => toCoverageSummary(this.value() as PersistentObject));
}
