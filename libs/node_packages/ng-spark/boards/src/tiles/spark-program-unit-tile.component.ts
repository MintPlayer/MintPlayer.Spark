import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';
import { ProgramUnit } from '@mintplayer/ng-spark/models';
import { ResolveTranslationPipe, RouterLinkPipe } from '@mintplayer/ng-spark/pipes';
import { SparkService } from '@mintplayer/ng-spark/services';
import { SparkBoardTile, SparkTileContext } from '../spark-board.models';

/**
 * The `programUnit` kind: a launcher for one menu entry. The unit is looked up in the caller's own
 * program units, which the server already filtered by rights, so a tile never links to a page the
 * menu would hide.
 */
@Component({
  selector: 'spark-program-unit-tile',
  imports: [RouterLink, RouterLinkPipe, ResolveTranslationPipe, SparkIconComponent],
  styleUrl: './spark-tiles.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (unit(); as u) {
      @if (u | routerLink; as link) {
        <a class="spark-tile-launch" [routerLink]="link">
          @if (u.icon) { <spark-icon [name]="u.icon" /> }
          <span>{{ u.name | resolveTranslation }}</span>
        </a>
      } @else {
        <a class="spark-tile-launch" [href]="u.url">
          @if (u.icon) { <spark-icon [name]="u.icon" /> }
          <span>{{ u.name | resolveTranslation }}</span>
        </a>
      }
    }
  `,
})
export class SparkProgramUnitTileComponent {
  private readonly spark = inject(SparkService);

  readonly tile = input.required<SparkBoardTile>();
  readonly context = input.required<SparkTileContext>();

  private readonly units = signal<ProgramUnit[]>([]);
  protected readonly unit = computed(() => this.units().find(u => u.id === this.tile().target['programUnit']) ?? null);

  constructor() {
    effect(onCleanup => {
      this.context().reloadToken;
      let live = true;
      onCleanup(() => live = false);
      this.spark.getProgramUnits().then(
        config => { if (live) this.units.set(config.programUnitGroups.flatMap(g => g.programUnits)); },
        () => { if (live) this.units.set([]); },
      );
    });
  }
}
