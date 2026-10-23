import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { SparkQueryGridComponent } from '@mintplayer/ng-spark/grid';
import { SparkBoardTile, SparkTileContext } from '../spark-board.models';

/** The `query` kind: the query's grid, paged and sortable, rows opening their detail page. */
@Component({
  selector: 'spark-query-tile',
  imports: [SparkQueryGridComponent],
  styleUrl: './spark-tiles.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<spark-query-grid [queryId]="queryId()" [reloadToken]="context().reloadToken" />`,
})
export class SparkQueryTileComponent {
  readonly tile = input.required<SparkBoardTile>();
  readonly context = input.required<SparkTileContext>();

  protected readonly queryId = computed(() => this.tile().target['query'] ?? '');
}
