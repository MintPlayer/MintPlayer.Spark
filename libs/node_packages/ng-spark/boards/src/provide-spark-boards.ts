import { Route } from '@angular/router';
import { sparkFeature, SparkFeature, SparkFeatureOrder } from '@mintplayer/ng-spark';
import { withDetailActions } from '@mintplayer/ng-spark/panels';
import { SparkBoardComponent } from './spark-board.component';
import { SPARK_TILE_KINDS } from './spark-board.models';
import { SPARK_BOARDS_OPTIONS, SparkBoardsOptions } from './spark-boards.options';
import { SparkPinButtonComponent } from './spark-pin-button.component';
import { withTileRenderers } from './spark-tile-renderers';
import { SparkCountTileComponent } from './tiles/spark-count-tile.component';
import { SparkPoTileComponent } from './tiles/spark-po-tile.component';
import { SparkProgramUnitTileComponent } from './tiles/spark-program-unit-tile.component';
import { SparkQueryTileComponent } from './tiles/spark-query-tile.component';

/**
 * Boards on the client (#530), for `provideSpark(...)`: the four built-in tile renderers and the detail page's
 * **Pin to dashboard** action. The server side is `MintPlayer.Spark.Boards` (`spark.AddBoards()`).
 * An app's own kinds register with {@link withTileRenderers}.
 */
export function withBoards(options: SparkBoardsOptions = {}): SparkFeature {
  return sparkFeature('boards', SparkFeatureOrder.modules, [
    { provide: SPARK_BOARDS_OPTIONS, useValue: options },
    ...withTileRenderers({
      [SPARK_TILE_KINDS.query]: SparkQueryTileComponent,
      [SPARK_TILE_KINDS.count]: SparkCountTileComponent,
      [SPARK_TILE_KINDS.persistentObject]: SparkPoTileComponent,
      [SPARK_TILE_KINDS.programUnit]: SparkProgramUnitTileComponent,
    }).providers,
    ...(options.pinAction === false ? [] : withDetailActions({ id: 'spark-boards-pin', component: SparkPinButtonComponent, priority: 80 }).providers),
  ]);
}

/** A route that shows one board: `sparkBoardRoute('', 'home')`. */
export function sparkBoardRoute(path: string, boardId: string, extra: Partial<Route> = {}): Route {
  return { ...extra, path, component: SparkBoardComponent, data: { ...extra.data, boardId } };
}
