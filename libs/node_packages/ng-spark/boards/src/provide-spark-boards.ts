import { Provider } from '@angular/core';
import { Route } from '@angular/router';
import { provideSparkDetailActions } from '@mintplayer/ng-spark/panels';
import { SparkBoardComponent } from './spark-board.component';
import { SPARK_TILE_KINDS } from './spark-board.models';
import { SPARK_BOARDS_OPTIONS, SparkBoardsOptions } from './spark-boards.options';
import { SparkPinButtonComponent } from './spark-pin-button.component';
import { provideSparkTileRenderers } from './spark-tile-renderers';
import { SparkCountTileComponent } from './tiles/spark-count-tile.component';
import { SparkPoTileComponent } from './tiles/spark-po-tile.component';
import { SparkProgramUnitTileComponent } from './tiles/spark-program-unit-tile.component';
import { SparkQueryTileComponent } from './tiles/spark-query-tile.component';

/**
 * Boards on the client (#530): the four built-in tile renderers and the detail page's
 * **Pin to dashboard** action. The server side is `MintPlayer.Spark.Boards` (`spark.AddBoards()`).
 * An app's own kinds register with {@link provideSparkTileRenderers}.
 */
export function provideSparkBoards(options: SparkBoardsOptions = {}): Provider[] {
  return [
    { provide: SPARK_BOARDS_OPTIONS, useValue: options },
    ...provideSparkTileRenderers({
      [SPARK_TILE_KINDS.query]: SparkQueryTileComponent,
      [SPARK_TILE_KINDS.count]: SparkCountTileComponent,
      [SPARK_TILE_KINDS.persistentObject]: SparkPoTileComponent,
      [SPARK_TILE_KINDS.programUnit]: SparkProgramUnitTileComponent,
    }),
    ...(options.pinAction === false ? [] : provideSparkDetailActions({ id: 'spark-boards-pin', component: SparkPinButtonComponent, priority: 80 })),
  ];
}

/** A route that shows one board: `sparkBoardRoute('', 'home')`. */
export function sparkBoardRoute(path: string, boardId: string, extra: Partial<Route> = {}): Route {
  return { ...extra, path, component: SparkBoardComponent, data: { ...extra.data, boardId } };
}
