import { ChangeDetectionStrategy, Component, computed, effect, ElementRef, inject, signal, viewChild } from '@angular/core';
import { CdkDrag, CdkDragHandle } from '@angular/cdk/drag-drop';
import { FormsModule } from '@angular/forms';
import { VideoPlayerComponent } from '@mintplayer/ng-video-player';
import { EPlayerState } from '@mintplayer/player-provider';
import { BsOffcanvasComponent, BsOffcanvasHeaderDirective } from '@mintplayer/ng-bootstrap/offcanvas';
import { BsListGroupComponent, BsListGroupItemComponent } from '@mintplayer/ng-bootstrap/list-group';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { BsCardBodyComponent, BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { ERepeatMode } from '@mintplayer/playlist-controller';
import { BsProgressBarComponent, BsProgressComponent } from '@mintplayer/ng-bootstrap/progress-bar';
import { BsModalComponent, BsModalFooterDirective, BsModalHeaderDirective } from '@mintplayer/ng-bootstrap/modal';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { PlayerService } from './player.service';
import { VideoApis } from './video-apis';

/** The floating player and its queue (G7): shown while the queue has a current item. */
@Component({
  selector: 'app-player-dock',
  imports: [
    VideoPlayerComponent, BsOffcanvasComponent, BsOffcanvasHeaderDirective, BsListGroupComponent, BsListGroupItemComponent,
    BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent, BsButtonTypeDirective, BsProgressComponent, BsProgressBarComponent,
    BsModalComponent, BsModalHeaderDirective, BsModalFooterDirective, BsFormComponent, BsFormControlDirective, FormsModule, CdkDrag, CdkDragHandle,
  ],
  templateUrl: './player-dock.component.html',
  styleUrl: './player-dock.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PlayerDockComponent {
  protected readonly player = inject(PlayerService);
  protected readonly colors = Color;
  protected readonly repeatModes = ERepeatMode;
  private readonly videoApis = inject(VideoApis);
  private readonly videoPlayer = viewChild(VideoPlayerComponent);

  constructor() {
    // Through setUrl rather than the url input: the input can't see the same url again, which is how the controller
    // restarts a medium (repeatOne, or previous after 5 seconds).
    effect(() => {
      const { item } = this.player.play();
      // The previous medium's state isn't this one's, and a player that never reports one would keep it.
      this.player.state.set(EPlayerState.unstarted);
      this.player.position.set({ currentTime: 0, duration: 0 });
      if (item) this.videoPlayer()?.setUrl(item.url);
    });
  }

  /** Runs after the two-way binding has stored the state (it is bound first in the template). */
  protected onStateChange(state: EPlayerState) {
    if (state === EPlayerState.ended) this.player.ended();
  }

  /** The current medium's position as a share of its length, 0 while the length is unknown. */
  protected readonly percent = computed(() => {
    const { currentTime, duration } = this.player.position();
    return duration > 0 ? Math.min(100, (currentTime / duration) * 100) : 0;
  });

  /** m:ss, or h:mm:ss for a medium of an hour or more. */
  protected time(seconds: number): string {
    const total = Math.max(0, Math.floor(seconds || 0));
    const h = Math.floor(total / 3600), m = Math.floor((total % 3600) / 60), s = total % 60;
    const ss = String(s).padStart(2, '0');
    return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${ss}` : `${m}:${ss}`;
  }

  // Add a URL to the queue (the legacy sidebar's "Add URL" modal): any URL one of the player's sites can play.
  protected readonly addUrlOpen = signal(false);
  protected readonly urlToAdd = signal('');
  /** The input as it was when refused: the error shows until the text changes. */
  private readonly rejectedUrl = signal<string | null>(null);
  protected readonly urlInvalid = computed(() => this.rejectedUrl() === this.urlToAdd());
  private readonly urlInput = viewChild<ElementRef<HTMLInputElement>>('urlInput');

  protected openAddUrl() {
    this.urlToAdd.set('');
    this.rejectedUrl.set(null);
    this.addUrlOpen.set(true);
    setTimeout(() => this.urlInput()?.nativeElement.focus(), 20);
  }

  protected addUrl() {
    const url = this.urlToAdd().trim();
    if (!this.videoApis.isPlayable(url)) {
      this.rejectedUrl.set(this.urlToAdd());
      return;
    }
    this.player.add({ url, title: url });
    this.addUrlOpen.set(false);
  }
}
