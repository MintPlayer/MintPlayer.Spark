import { ChangeDetectionStrategy, Component, computed, inject, input, signal, effect } from '@angular/core';
import type { QueryResultItem, SparkRow } from '@mintplayer/ng-spark/models';
import { valueFor } from '@mintplayer/ng-spark/models';
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
  /**
   * The whole row (grid) or the whole persistent object (detail page). This is where the forge
   * comes from — see the effect below.
   */
  item = input<QueryResultItem | Record<string, any> | undefined>();

  readonly points = signal<number[] | null>(null);

  private readonly fullName = computed(() => (typeof this.value() === 'string' ? this.value() as string : ''));

  /** The canonical forge spelling for this row: its OwnerKey's prefix, or an explicit override. */
  private readonly provider = computed(() => {
    const row = this.item();
    const ownerKey = row ? valueFor(row as SparkRow, 'OwnerKey')?.value : undefined;
    if (typeof ownerKey === 'string' && ownerKey.includes(':')) return ownerKey.split(':')[0];

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
      // whole row, and Repository carries OwnerKey ("github:mintplayer") on it.
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
