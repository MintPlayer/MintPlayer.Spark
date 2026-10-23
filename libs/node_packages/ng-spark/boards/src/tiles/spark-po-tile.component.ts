import { ChangeDetectionStrategy, Component, computed, effect, input, untracked, viewChild } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkPoDetailComponent } from '@mintplayer/ng-spark/po-detail';
import { SparkBoardTile, SparkTileContext } from '../spark-board.models';

/**
 * The `persistentObject` kind (D15): one object, read-only. It is the detail page's own attribute
 * view (`spark-po-detail` with the object as inputs), without the action bar, title, sub-queries and
 * panels, and a link to that page.
 */
@Component({
  selector: 'spark-po-tile',
  imports: [RouterLink, SparkPoDetailComponent, TranslateKeyPipe],
  styleUrl: './spark-tiles.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <spark-po-detail [objectType]="entityType()" [objectId]="id()" [showQueries]="false" [showChrome]="false" [compact]="true" />
    <a class="spark-tile-open" [routerLink]="['/po', entityType(), id()]">{{ 'boards.spa.open' | t }}</a>
  `,
})
export class SparkPoTileComponent {
  readonly tile = input.required<SparkBoardTile>();
  readonly context = input.required<SparkTileContext>();

  protected readonly entityType = computed(() => this.tile().target['entityType'] ?? '');
  protected readonly id = computed(() => this.tile().target['id'] ?? '');
  private readonly detail = viewChild(SparkPoDetailComponent);

  constructor() {
    // The board's refresh: the first run is the initial load, which the detail component does itself.
    let first = true;
    effect(() => {
      this.context().reloadToken;
      if (first) { first = false; return; }
      untracked(() => this.detail()?.load());
    });
  }
}
