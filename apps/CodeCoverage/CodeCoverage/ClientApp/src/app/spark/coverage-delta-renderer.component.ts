import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { SparkAttributeColumnRenderer, SparkAttributeDetailRenderer } from '@mintplayer/ng-spark/renderers';
import { formatDelta } from './coverage-delta';

/**
 * Spark attribute renderer "coverage-delta": a commit's coverage change in
 * percentage points, green when up and red when down, a dash when the commit
 * has no reference to compare against. Bound to the two persisted deltas on
 * Commit (vs the git parent, vs the default branch), which the assembler stamps
 * once — a cell needs no knowledge of its neighbouring rows, and re-sorting or
 * paging the grid cannot change what a row's number means.
 */
@Component({
  selector: 'app-coverage-delta-renderer',
  template: `
    <span class="small" [class.text-success]="delta().up" [class.text-danger]="delta().down" [class.text-muted]="!delta().up && !delta().down">{{ delta().text }}</span>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class CoverageDeltaRendererComponent implements SparkAttributeColumnRenderer, SparkAttributeDetailRenderer {
  value = input<any>();
  options = input<Record<string, any> | undefined>();

  readonly delta = computed(() => formatDelta(this.value()));
}
