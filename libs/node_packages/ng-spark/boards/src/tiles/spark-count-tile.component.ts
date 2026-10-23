import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkService } from '@mintplayer/ng-spark/services';
import { SparkBoardTile, SparkTileContext } from '../spark-board.models';

/**
 * The `count` kind (D14): the query's row count, read as `totalItems` of a one-row page through the
 * ordinary execute endpoint, so row-level security counts exactly what the grid would list.
 */
@Component({
  selector: 'spark-count-tile',
  imports: [RouterLink, TranslateKeyPipe],
  styleUrl: './spark-tiles.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <a class="spark-tile-count" [routerLink]="['/query', queryId()]">
      @if (count(); as value) {
        <span class="spark-tile-count-value">{{ value.total }}</span>
      } @else if (failed()) {
        <span class="spark-tile-muted" role="alert">{{ 'boards.spa.loadFailed' | t }}</span>
      } @else {
        <span class="spark-tile-count-value spark-tile-muted">…</span>
      }
      <span class="spark-tile-count-label">{{ 'boards.spa.items' | t }}</span>
    </a>
  `,
})
export class SparkCountTileComponent {
  private readonly spark = inject(SparkService);

  readonly tile = input.required<SparkBoardTile>();
  readonly context = input.required<SparkTileContext>();

  protected readonly queryId = computed(() => this.tile().target['query'] ?? '');
  protected readonly count = signal<{ total: number } | null>(null);
  protected readonly failed = signal(false);

  constructor() {
    effect(onCleanup => {
      const queryId = this.queryId();
      this.context().reloadToken;
      let live = true;
      onCleanup(() => live = false);
      this.failed.set(false);
      this.spark.executeQuery(queryId, { skip: 0, take: 1 }).then(
        result => { if (live) this.count.set({ total: result.totalItems }); },
        () => { if (live) this.failed.set(true); },
      );
    });
  }
}
