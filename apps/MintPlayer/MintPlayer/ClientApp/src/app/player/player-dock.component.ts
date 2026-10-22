import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { VideoPlayerComponent } from '@mintplayer/ng-video-player';
import { EPlayerState } from '@mintplayer/player-provider';
import { BsOffcanvasCloseDirective, BsOffcanvasContentDirective, BsOffcanvasHostComponent, OffcanvasBodyComponent, OffcanvasHeaderComponent } from '@mintplayer/ng-bootstrap/offcanvas';
import { BsCloseComponent } from '@mintplayer/ng-bootstrap/close';
import { BsListGroupComponent, BsListGroupItemComponent } from '@mintplayer/ng-bootstrap/list-group';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { BsCardBodyComponent, BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { PlayerService } from './player.service';

/** The floating player and its queue (G7): shown while the queue has a current item. */
@Component({
  selector: 'app-player-dock',
  imports: [VideoPlayerComponent, BsOffcanvasHostComponent, BsOffcanvasContentDirective, BsOffcanvasCloseDirective, OffcanvasHeaderComponent, OffcanvasBodyComponent, BsCloseComponent, BsListGroupComponent, BsListGroupItemComponent, BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent, BsButtonTypeDirective],
  templateUrl: './player-dock.component.html',
  styleUrl: './player-dock.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PlayerDockComponent {
  protected readonly player = inject(PlayerService);
  protected readonly colors = Color;

  protected onStateChange(state: EPlayerState) {
    if (state === EPlayerState.ended) this.player.next();
  }
}
