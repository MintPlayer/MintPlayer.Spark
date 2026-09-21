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
      // An attribute renderer sees only its own value, so the forge has to be handed to it
      // through the model's type hints. Absent, this renders nothing rather than assuming a
      // forge: a missing sparkline is a visual gap, while guessing would show one owner's
      // coverage against a same-named owner on another forge. ⚠️ M7 leaves this ungated
      // because the repositories grid is not provider-scoped yet - that is D4's sidebar work.
      const provider = this.options()?.['provider'];
      if (!owner || typeof provider !== 'string' || !provider) {
        this.points.set(null);
        return;
      }
      const cacheKey = provider + ':' + owner;
      let batch = sparklinesByOwner.get(cacheKey);
      if (!batch) {
        batch = this.browse.getSparklines(provider, owner).catch(() => ({} as Record<string, number[]>));
        sparklinesByOwner.set(cacheKey, batch);
      }
      const lines = await batch;
      this.points.set(lines[fullName] ?? null);
    });
  }
}
