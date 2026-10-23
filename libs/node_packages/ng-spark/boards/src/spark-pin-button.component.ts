import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { SparkDetailContext } from '@mintplayer/ng-spark/panels';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { addTile, startDraft, toSaveRequest } from './board-draft';
import { SPARK_TILE_KINDS } from './spark-board.models';
import { SPARK_BOARDS_OPTIONS } from './spark-boards.options';
import { SparkBoardsService } from './spark-boards.service';

/**
 * **Pin to dashboard** on the detail page of every object (#530). Registered by
 * {@link provideSparkBoards} as a `SPARK_DETAIL_ACTIONS` entry. It adds a `persistentObject` tile
 * for this object to the pin board (`home` unless configured), once: an object already pinned is
 * left alone. The server decides whether the caller may customize, and whether the tile is allowed.
 */
@Component({
  selector: 'spark-pin-button',
  imports: [BsButtonTypeDirective, TranslateKeyPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (context().deleted !== 'only') {
      <span class="spark-pin">
        <button type="button" class="spark-pin-button rounded-0" [color]="colors.secondary" [disabled]="busy() || done()" (click)="pin()">
          {{ 'boards.spa.pin' | t }}
        </button>
        @if (done()) {
          <span class="spark-pin-done">{{ 'boards.spa.pinned' | t }}</span>
        }
        @if (error(); as message) {
          <span class="spark-pin-error" role="alert">{{ message }}</span>
        }
      </span>
    }
  `,
})
export class SparkPinButtonComponent {
  private readonly boards = inject(SparkBoardsService);
  private readonly lang = inject(SparkLanguageService);
  private readonly boardId = inject(SPARK_BOARDS_OPTIONS, { optional: true })?.pinBoard ?? 'home';

  context = input.required<SparkDetailContext>();

  protected readonly colors = Color;
  protected readonly busy = signal(false);
  protected readonly done = signal(false);
  protected readonly error = signal<string | null>(null);

  async pin(): Promise<void> {
    const ctx = this.context();
    const target = { entityType: ctx.type, id: ctx.id };
    this.busy.set(true);
    this.error.set(null);
    try {
      const board = await this.boards.get(this.boardId);
      const pinned = board.tiles.some(t => t.kind === SPARK_TILE_KINDS.persistentObject
        && t.target['entityType'] === target.entityType && t.target['id'] === target.id);
      if (!pinned) {
        const draft = addTile(startDraft(board), { kind: SPARK_TILE_KINDS.persistentObject, target });
        await this.boards.save(this.boardId, toSaveRequest(draft, board.changeVector));
      }
      this.done.set(true);
    } catch (e) {
      this.error.set(this.lang.t(e instanceof HttpErrorResponse && e.status === 409 ? 'boards.spa.conflict' : 'boards.spa.saveFailed'));
    } finally {
      this.busy.set(false);
    }
  }
}
