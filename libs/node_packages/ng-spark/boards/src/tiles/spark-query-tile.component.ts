import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { SparkQueryGridComponent } from '@mintplayer/ng-spark/grid';
import { SparkBoardTile, SparkTileContext } from '../spark-board.models';

/**
 * The `query` kind: the query's grid, paged and sortable, rows opening their detail page. The grid
 * fills the tile: header at the top, pager at the bottom, only the rows scroll in between.
 */
@Component({
  selector: 'spark-query-tile',
  imports: [SparkQueryGridComponent],
  styleUrl: './spark-tiles.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<spark-query-grid fill [queryId]="queryId()" [reloadToken]="context().reloadToken" />`,
})
export class SparkQueryTileComponent {
  readonly tile = input.required<SparkBoardTile>();
  readonly context = input.required<SparkTileContext>();

  protected readonly queryId = computed(() => this.tile().target['query'] ?? '');
}
