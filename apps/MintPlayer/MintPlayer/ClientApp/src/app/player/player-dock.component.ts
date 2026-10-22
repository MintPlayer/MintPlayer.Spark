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
      // The previous medium's state isn't this one's, and a player that never reports one would keep it.
      this.player.state.set(EPlayerState.unstarted);
      if (item) this.videoPlayer()?.setUrl(item.url);
    });
  }

  /** Runs after the two-way binding has stored the state (it is bound first in the template). */
  protected onStateChange(state: EPlayerState) {
    if (state === EPlayerState.ended) this.player.ended();
  }
}
