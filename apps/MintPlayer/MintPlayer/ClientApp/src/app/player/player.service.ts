import { computed, Injectable, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { EPlayerState } from '@mintplayer/player-provider';
import { PlayerProgress } from '@mintplayer/player-progress';
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
  /** Two-way bound to the video player: it reports its state here, and setting it plays or pauses. */
  readonly state = signal(EPlayerState.unstarted);
  readonly isPlaying = computed(() => this.state() === EPlayerState.playing);
  /** Where the current medium is, in seconds, for the queue's progress bar (as the legacy playlist sidebar showed). */
  readonly position = signal<PlayerProgress>({ currentTime: 0, duration: 0 });
  /**
   * 0-100, two-way bound to the video player, which keeps both across media and sites since video-player 20.1.1
   * (mintplayer-ng-video-player#105).
   */
  readonly volume = signal(100);
  readonly muted = signal(false);

  add(item: QueueItem) {
    this.controller.addToPlaylist(item);
    this.refresh();
  }

  remove(index: number) {
    this.controller.removeFromPlaylist(this.queue()[index]);
    this.refresh();
  }

  /**
   * One by one, the current item last: removing the last item ends playback. setPlaylist([]) does not, because it
   * keeps the current item playing.
   */
  clear() {
    const current = this.current();
    for (const item of this.queue()) if (item !== current) this.controller.removeFromPlaylist(item);
    if (current) this.controller.removeFromPlaylist(current);
    this.refresh();
  }

  togglePlay() {
    if (this.current()) this.state.set(this.isPlaying() ? EPlayerState.paused : EPlayerState.playing);
  }

  previous() {
    this.controller.previous();
  }

  next() {
    this.controller.next();
  }

  /**
   * Leaves `ended` out of the bound state: the binding would push it back into the player, and the YouTube player
   * stops on `ended`, which stopped the next medium. `unstarted` is a no-op in every player.
   */
  ended() {
    this.state.set(EPlayerState.unstarted);
    this.controller.playerEnded();
  }

  /** Feeds the progress bar, and lets previous restart the current medium once it has played 5 seconds. */
  progress(position: PlayerProgress) {
    this.position.set({ currentTime: position.currentTime, duration: position.duration });
    this.controller.currentVideoPosition = position.currentTime;
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
