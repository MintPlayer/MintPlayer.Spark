import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { AS_DETAIL_BREADCRUMBS_KEY, EntityAttributeDefinition, PersistentObject } from '@mintplayer/ng-spark/models';
import { SparkAttributeRowRenderer } from '@mintplayer/ng-spark/renderers';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { PlayerService } from '../player/player.service';
import { VideoApis } from '../player/video-apis';

/**
 * The leading cell of a Media table (`leadingComponent` of `media-player`): a play button when the player can
 * play the medium's URL. Like the legacy media list, it adds the medium to the queue.
 */
@Component({
  selector: 'app-media-play-button',
  imports: [BsButtonTypeDirective],
  templateUrl: './media-play-button.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MediaPlayButtonComponent implements SparkAttributeRowRenderer {
  readonly row = input<Record<string, any>>({});
  readonly attributes = input<EntityAttributeDefinition[]>([]);
  readonly owner = input<PersistentObject>();

  protected readonly colors = Color;
  private readonly player = inject(PlayerService);
  private readonly videoApis = inject(VideoApis);

  protected readonly url = computed(() => {
    const name = this.attributes()[0]?.name;
    const value = name ? this.row()[name] : undefined;
    return typeof value === 'string' ? value : null;
  });

  protected readonly playable = computed(() => this.videoApis.isPlayable(this.url()));

  protected play() {
    const url = this.url();
    if (!url) return;
    const owner = this.owner();
    // The medium's type ("Official Audio"), as the server resolved it for the Type column.
    const type = (this.row()[AS_DETAIL_BREADCRUMBS_KEY] as Record<string, string> | undefined)?.['TypeId'];
    this.player.add({ url, title: owner?.breadcrumb || owner?.name || url, subtitle: type, ownerId: owner?.id });
  }
}
