import { ChangeDetectionStrategy, Component, computed, inject, input, signal, effect } from '@angular/core';
import type { SparkAttributeColumnRenderer, SparkAttributeDetailRenderer } from '@mintplayer/ng-spark/renderers';
import { BsSparklineComponent } from '@mintplayer/ng-bootstrap/charts/sparkline';
import { BrowseService } from '../services/browse.service';

/**
 * Spark attribute renderer "coverage-sparkline": bound to Repository.FullName
 * (label "Trend"), it renders the repo's recent coverage percentages as a
 * sparkline. The owner's sparkline batch (/api/browse/accounts/{owner}/sparklines)
 * is fetched once per owner and shared across all cells via a module-level
 * promise cache — the owner is simply the first segment of the fullName value.
 */
const sparklinesByOwner = new Map<string, Promise<Record<string, number[]>>>();

@Component({
  selector: 'app-coverage-sparkline-renderer',
  imports: [BsSparklineComponent],
  template: `
    @let p = points();
    @if (p && p.length > 1) {
      <bs-sparkline style="width: 90px; height: 24px;" class="d-inline-block"
                    [points]="p" [yMin]="0" [yMax]="100"
                    [inputLabel]="'Coverage trend for ' + (value() ?? '')" />
    } @else {
      <span class="text-muted small">—</span>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class CoverageSparklineRendererComponent implements SparkAttributeColumnRenderer, SparkAttributeDetailRenderer {
  private readonly browse = inject(BrowseService);

  value = input<any>();
  options = input<Record<string, any> | undefined>();

  readonly points = signal<number[] | null>(null);

  private readonly fullName = computed(() => (typeof this.value() === 'string' ? this.value() as string : ''));

  constructor() {
    effect(async () => {
      const fullName = this.fullName();
      const owner = fullName.split('/')[0];
      if (!owner) {
        this.points.set(null);
        return;
      }
      let batch = sparklinesByOwner.get(owner);
      if (!batch) {
        batch = this.browse.getSparklines(owner).catch(() => ({} as Record<string, number[]>));
        sparklinesByOwner.set(owner, batch);
      }
      const lines = await batch;
      this.points.set(lines[fullName] ?? null);
    });
  }
}
