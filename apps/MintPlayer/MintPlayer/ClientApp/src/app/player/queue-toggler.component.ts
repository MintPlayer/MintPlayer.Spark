import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { PlayerService } from './player.service';

/** Opens and closes the queue offcanvas from the top bar, like the legacy playlist-toggler. */
@Component({
  selector: 'app-queue-toggler',
  imports: [BsButtonTypeDirective],
  template: `
    <button type="button" [color]="colors.secondary" class="text-nowrap" title="Queue" aria-label="Toggle the queue" [attr.aria-expanded]="player.queueOpen()" (click)="player.queueOpen.set(!player.queueOpen())">
      <i class="bi bi-music-note-list"></i>
      @if (player.queue().length) { {{ player.queue().length }} }
    </button>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class QueueTogglerComponent {
  protected readonly player = inject(PlayerService);
  protected readonly colors = Color;
}
