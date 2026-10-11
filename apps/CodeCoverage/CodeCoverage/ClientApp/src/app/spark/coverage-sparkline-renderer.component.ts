import { ChangeDetectionStrategy, Component, computed, inject, input, signal, effect } from '@angular/core';
import type { SparkAttributeColumnRenderer, SparkAttributeDetailRenderer } from '@mintplayer/ng-spark/renderers';
import { SparkTrendOptions, SparkTrendSparklineComponent } from '@mintplayer/ng-spark/score-renderers';
import { BrowseService } from '../services/browse.service';
import { forgeOf } from './forge-of';

/**
 * Spark attribute renderer "coverage-sparkline": bound to Repository.FullName
 * (label "Trend"), it renders the repo's recent coverage percentages as a
 * sparkline. The owner's sparkline batch (/api/browse/accounts/{owner}/sparklines)
 * is fetched once per owner and shared across all cells via a module-level
 * promise cache — the owner is simply the first segment of the fullName value.
 */
const sparklinesByOwner = new Map<string, Promise<Record<string, number[]>>>();

/** Specs only: the memo above outlives a TestBed, so each test starts from an empty one. */
export function clearSparklineCacheForTesting(): void { sparklinesByOwner.clear(); }

@Component({
  selector: 'app-coverage-sparkline-renderer',
  imports: [SparkTrendSparklineComponent],
  // ng-spark's trend sparkline draws the line (and the dash for fewer than two points); this
  // component only finds the series. Options spelled out, not left to the renderer's defaults.
  template: `<spark-trend-sparkline [value]="points()" [options]="trendOptions()" />`,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class CoverageSparklineRendererComponent implements SparkAttributeColumnRenderer, SparkAttributeDetailRenderer {
  private readonly browse = inject(BrowseService);

  value = input<any>();
  options = input<Record<string, any> | undefined>();
  /**
   * The whole row (grid) or the whole persistent object (detail page). This is where the forge
   * comes from — see the effect below.
   *
   * ⚠ Deliberately `any`, like `value` above, and a union will NOT do. This component implements
   * both renderer contracts, whose `item` differ - QueryResultItem for a column, PersistentObject
   * for a detail page - and InputSignal is invariant in its type argument because of transformFn,
   * so no single union satisfies both. `valueFor` accepts either shape, which is what SparkRow is,
   * and the cast at the read site is where the type comes back.
   */
  item = input<any>();

  readonly points = signal<number[] | null>(null);

  protected readonly trendOptions = computed<Record<string, any>>(() => ({
    display: 'sparkline',
    yMin: 0,
    yMax: 100,
    width: '90px',
    height: '24px',
    minPoints: 2,
    label: 'Coverage trend for ' + (this.value() ?? ''),
  } satisfies SparkTrendOptions));

  private readonly fullName = computed(() => (typeof this.value() === 'string' ? this.value() as string : ''));

  /** The canonical forge spelling for this row: from its document id, or an explicit override. */
  private readonly provider = computed(() => {
    const fromId = forgeOf(this.item());
    if (fromId) return fromId;

    const override = this.options()?.['provider'];
    return typeof override === 'string' && override.length > 0 ? override : null;
  });

  constructor() {
    effect(async () => {
      const fullName = this.fullName();
      const owner = fullName.split('/')[0];
      // The forge comes from the ROW, not from this cell. A column renderer receives only its own
      // value by default, which is why an earlier version of this took the provider from the
      // model's static type hints and rendered NOTHING without one - a blank column on every row,
      // since a type hint is per-attribute and the forge is per-row. Declaring `item` gets the
      // whole row, and a Repository row's id ("Repositories/github/123") names the forge.
      //
      // Still no guessing when the row has no forge: a sparkline fetched for a guessed provider
      // would show one owner's coverage against a same-named owner on another forge. A missing
      // sparkline is a visual gap; a wrong one is a lie. `options.provider` stays supported as an
      // override for hosts that render this outside a row.
      const provider = this.provider();
      if (!owner || !provider) {
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
