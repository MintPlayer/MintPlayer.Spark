import { ChangeDetectionStrategy, Component, effect, inject, viewChild } from '@angular/core';
import { CdkDrag, CdkDragHandle } from '@angular/cdk/drag-drop';
import { VideoPlayerComponent } from '@mintplayer/ng-video-player';
import { EPlayerState } from '@mintplayer/player-provider';
import { BsOffcanvasCloseDirective, BsOffcanvasContentDirective, BsOffcanvasHostComponent, OffcanvasBodyComponent, OffcanvasHeaderComponent } from '@mintplayer/ng-bootstrap/offcanvas';
import { BsCloseComponent } from '@mintplayer/ng-bootstrap/close';
import { BsListGroupComponent, BsListGroupItemComponent } from '@mintplayer/ng-bootstrap/list-group';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { BsCardBodyComponent, BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { ERepeatMode } from '@mintplayer/playlist-controller';
import { PlayerService } from './player.service';

/** The floating player and its queue (G7): shown while the queue has a current item. */
@Component({
  selector: 'app-player-dock',
  imports: [VideoPlayerComponent, BsOffcanvasHostComponent, BsOffcanvasContentDirective, BsOffcanvasCloseDirective, OffcanvasHeaderComponent, OffcanvasBodyComponent, BsCloseComponent, BsListGroupComponent, BsListGroupItemComponent, BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent, BsButtonTypeDirective, CdkDrag, CdkDragHandle],
  templateUrl: './player-dock.component.html',
  styleUrl: './player-dock.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PlayerDockComponent {
  protected readonly player = inject(PlayerService);
  protected readonly colors = Color;
  protected readonly repeatModes = ERepeatMode;
  private readonly videoPlayer = viewChild(VideoPlayerComponent);

  constructor() {
    // Through setUrl rather than the url input: the input can't see the same url again, which is how the controller
    // restarts a medium (repeatOne, or previous after 5 seconds).
    effect(() => {
      const { item } = this.player.play();
      if (item) this.videoPlayer()?.setUrl(item.url);
      else this.player.state.set(EPlayerState.unstarted);
    });
  }

  /** A command, not a binding: echoing the reported state back (ended → stopVideo) stopped the next medium. */
  protected togglePlay() {
    const videoPlayer = this.videoPlayer();
    if (videoPlayer && this.player.current()) videoPlayer.playerState = this.player.isPlaying() ? EPlayerState.paused : EPlayerState.playing;
  }

  protected onStateChange(state: EPlayerState) {
    this.player.state.set(state);
    if (state === EPlayerState.ended) this.player.ended();
  }
}
