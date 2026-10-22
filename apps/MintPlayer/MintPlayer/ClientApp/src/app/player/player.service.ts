import { computed, Injectable, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { EPlayerState } from '@mintplayer/player-provider';
import { ERepeatMode, PlaylistController } from '@mintplayer/playlist-controller';
import { map } from 'rxjs';

/** One queued medium: what plays, and the song (or artist) it came from. */
export interface QueueItem {
  url: string;
  /** The owner's breadcrumb, e.g. "Artist - Song". */
  title: string;
  /** The medium's type, e.g. "Official Audio". */
  subtitle?: string;
  ownerId?: string;
}

/**
 * The play queue behind the floating player (G7): signals over the playlist controller, which owns the queue, the
 * played history, shuffle and repeat. Its playlist holds its own copy of every added item, so a duplicate is told
 * apart by reference; when the queue runs out it emits null, which hides the player card.
 */
@Injectable({ providedIn: 'root' })
export class PlayerService {
  private readonly controller = new PlaylistController<QueueItem>();

  /** One value per emission, even when the same item plays again (repeatOne, or previous restarting it). */
  readonly play = toSignal(this.controller.video$.pipe(map(item => ({ item }))), { requireSync: true });
  readonly current = computed(() => this.play().item);
  /** The controller's playlist has no change notification, so every call that changes it refreshes this. */
  readonly queue = signal<QueueItem[]>([]);
  readonly index = computed(() => {
    const current = this.current();
    return current ? this.queue().indexOf(current) : -1;
  });
  readonly shuffle = signal(false);
  readonly repeat = signal(ERepeatMode.noRepeat);
  readonly queueOpen = signal(false);
  /** The state the video player reports; it is never written back to the player. */
  readonly state = signal(EPlayerState.unstarted);
  readonly isPlaying = computed(() => this.state() === EPlayerState.playing);

  add(item: QueueItem) {
    this.controller.addToPlaylist(item);
    this.refresh();
  }

  remove(index: number) {
    this.controller.removeFromPlaylist(this.queue()[index]);
    this.refresh();
  }

  clear() {
    this.controller.setPlaylist([]);
    this.refresh();
  }

  previous() {
    this.controller.previous();
  }

  next() {
    this.controller.next();
  }

  ended() {
    this.controller.playerEnded();
  }

  /** Lets previous restart the current medium once it has played 5 seconds. */
  progress(seconds: number) {
    this.controller.currentVideoPosition = seconds;
  }

  toggleShuffle() {
    this.shuffle.update(shuffle => !shuffle);
    this.controller.shuffle = this.shuffle();
  }

  /** noRepeat → repeatAll → repeatOne → noRepeat, as the legacy repeat button cycled. */
  cycleRepeat() {
    this.repeat.update(repeat =>
      repeat === ERepeatMode.noRepeat ? ERepeatMode.repeatAll : repeat === ERepeatMode.repeatAll ? ERepeatMode.repeatOne : ERepeatMode.noRepeat);
    this.controller.repeat = this.repeat();
  }

  private refresh() {
    this.queue.set(this.controller.playlist);
  }
}
