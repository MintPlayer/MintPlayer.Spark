import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { PersistentObject } from '@mintplayer/ng-spark/models';
import { PlayerService } from '../../player/player.service';
import { mediumQueueItem } from '../../player/queue-item';

/**
 * The song page's header (#601): the thumbnail of the song's first playable medium, with a round play button that
 * adds that medium to the queue (D21, D22). The server picks both (`Thumbnail`, `ThumbnailMedium`, D15/D16), so the
 * header is in the server render and needs no player plugin to decide; no thumbnail, no header.
 */
@Component({
  selector: 'app-song-header',
  templateUrl: './song-header.component.html',
  styleUrl: './song-header.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SongHeaderComponent {
  readonly item = input.required<PersistentObject>();

  private readonly player = inject(PlayerService);

  protected readonly title = computed(() => this.item().breadcrumb || this.item().name);
  protected readonly thumbnail = computed(() => this.text('Thumbnail'));
  protected readonly medium = computed(() => this.text('ThumbnailMedium'));

  protected play() {
    const medium = this.medium();
    if (medium) this.player.add(mediumQueueItem(this.item(), medium));
  }

  private text(name: string): string | null {
    const value = this.item().attributes.find(a => a.name === name)?.value;
    return typeof value === 'string' && value.startsWith('https://') ? value : null;
  }
}
