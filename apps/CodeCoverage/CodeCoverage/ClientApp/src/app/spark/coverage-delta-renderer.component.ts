import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { SparkAttributeColumnRenderer, SparkAttributeDetailRenderer } from '@mintplayer/ng-spark/renderers';

/**
 * Spark attribute renderer "coverage-delta": the commit's coverage change in
 * percentage points, green when up and red when down — the Δ column the
 * hand-written commits table used to draw. Commit.CoverageDelta is computed by
 * the commits query over the whole ordered sequence, so a cell needs no
 * knowledge of its neighbouring rows.
 */
@Component({
  selector: 'app-coverage-delta-renderer',
  template: `
    @if (delta(); as d) {
      <span class="small" [class.text-success]="d.up" [class.text-danger]="d.down">{{ d.text }}</span>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class CoverageDeltaRendererComponent implements SparkAttributeColumnRenderer, SparkAttributeDetailRenderer {
  value = input<any>();
  options = input<Record<string, any> | undefined>();

  readonly delta = computed(() => {
    const value = Number(this.value());
    if (this.value() === null || this.value() === undefined || Number.isNaN(value)) return null;
    return { up: value > 0, down: value < 0, text: `${value > 0 ? '+' : ''}${value.toFixed(1)}` };
  });
}
