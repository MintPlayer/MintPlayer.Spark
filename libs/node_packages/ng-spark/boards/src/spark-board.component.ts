import { HttpErrorResponse } from '@angular/common/http';
import { NgComponentOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, Type, untracked, viewChild } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsButtonGroupComponent } from '@mintplayer/ng-bootstrap/button-group';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsModalBodyDirective, BsModalDirective, BsModalFooterDirective, BsModalHeaderDirective, BsModalHostComponent } from '@mintplayer/ng-bootstrap/modal';
import {
  BsTileContentDirective,
  BsTileDirective,
  BsTileHeaderDirective,
  BsTileManagerComponent,
  type MintTile,
  type TileLayoutChange,
  type TileLayouts,
} from '@mintplayer/ng-bootstrap/tile-manager';
import { ResolveTranslationPipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkLanguageService, SparkRenderPhase } from '@mintplayer/ng-spark/services';
import { addTile, applyToNarrower, commitLayout, removeTile, renameTile, SparkBoardDraft, startDraft, toSaveRequest } from './board-draft';
import { SparkBoardPickerComponent, SparkBoardPick } from './spark-board-picker.component';
import { SPARK_BOARD_PRERENDERED_TILES, SPARK_TILE_KINDS, SparkBoard, SparkBoardTile, SparkTileContext, toTileLayouts } from './spark-board.models';
import { SparkBoardsService } from './spark-boards.service';
import { SPARK_TILE_RENDERERS, tileRendererMap } from './spark-tile-renderers';

/** A tile as the tile manager holds it: one object per tile, kept while the tile is unchanged. */
interface SparkBoardTileItem extends MintTile {
  tile: SparkBoardTile;
}

/**
 * One board (#530): the caller's layout of `GET /spark/boards/{board}`, drawn by the tile manager.
 * The board is read-only until **Customize**; edits go to a draft (D9) that **Save** sends whole and
 * **Cancel** drops. A save after a layout edit that leaves narrower stored layouts behind asks
 * whether those widths should follow this one (§4.7).
 *
 * The board id is the `boardId` input, else the route's `data.boardId`.
 */
@Component({
  selector: 'spark-board',
  imports: [
    NgComponentOutlet, FormsModule, RouterLink,
    BsTileManagerComponent, BsTileDirective, BsTileHeaderDirective, BsTileContentDirective,
    BsButtonGroupComponent, BsButtonTypeDirective, BsFormComponent, BsFormControlDirective,
    BsModalHostComponent, BsModalDirective, BsModalHeaderDirective, BsModalBodyDirective, BsModalFooterDirective,
    ResolveTranslationPipe, TranslateKeyPipe, SparkBoardPickerComponent,
  ],
  templateUrl: './spark-board.component.html',
  styleUrl: './spark-board.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkBoardComponent {
  private readonly boards = inject(SparkBoardsService);
  private readonly lang = inject(SparkLanguageService);
  private readonly route = inject(ActivatedRoute, { optional: true });
  private readonly renderPhase = inject(SparkRenderPhase);
  private readonly renderers = tileRendererMap(inject(SPARK_TILE_RENDERERS, { optional: true }));

  readonly boardId = input<string | null>(null);

  protected readonly colors = Color;
  private readonly routeData = this.route ? toSignal(this.route.data) : signal(undefined);
  protected readonly id = computed(() => this.boardId() ?? (this.routeData()?.['boardId'] as string | undefined) ?? null);

  protected readonly board = signal<SparkBoard | null>(null);
  protected readonly draft = signal<SparkBoardDraft | null>(null);
  protected readonly loadFailed = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);
  private readonly reloadToken = signal(0);

  /** The column count on screen, from `columnCountChange`. */
  private readonly columnCount = signal<number | null>(null);
  /** The last edit's count and the narrower stored counts it did not reach. */
  private readonly lastEdit = signal<{ columnCount: number; staleCounts: number[] } | null>(null);

  protected readonly askNarrower = signal(false);
  protected readonly pickerOpen = signal(false);
  protected readonly renaming = signal<{ id: string; title: string } | null>(null);

  protected readonly editing = computed(() => this.draft() !== null);
  private readonly tileManager = viewChild(BsTileManagerComponent);

  protected readonly tiles = computed(() => this.draft()?.tiles ?? this.board()?.tiles ?? []);
  protected readonly layouts = computed<TileLayouts>(() => this.draft()?.layouts ?? toTileLayouts(this.board()?.layouts));

  private itemCache = new Map<string, SparkBoardTileItem>();
  protected readonly items = computed(() => {
    const editing = this.editing();
    const next = new Map<string, SparkBoardTileItem>();
    const items = this.tiles().map(tile => {
      const cached = this.itemCache.get(tile.id);
      const item = cached && cached.tile === tile && cached.disableMove === !editing
        ? cached
        : { id: tile.id, tile, label: this.title(tile), defaultSize: tile.defaultSize ?? undefined, disableMove: !editing, disableResize: !editing };
      next.set(tile.id, item);
      return item;
    });
    this.itemCache = next;
    return items;
  });

  /** Over {@link SPARK_BOARD_PRERENDERED_TILES} tiles, the server render and its hydration show links (D14). */
  protected readonly linkMode = computed(() => this.renderPhase.serverMatching() && this.tiles().length > SPARK_BOARD_PRERENDERED_TILES);

  /** The page a tile links to in link mode, or null for a kind that renders its content even then. */
  protected tileLink(tile: SparkBoardTile): string[] | null {
    switch (tile.kind) {
      case SPARK_TILE_KINDS.query:
      case SPARK_TILE_KINDS.count:
        return tile.target['query'] ? ['/query', tile.target['query']] : null;
      case SPARK_TILE_KINDS.persistentObject:
        return tile.target['entityType'] && tile.target['id'] ? ['/po', tile.target['entityType'], tile.target['id']] : null;
      default:
        return null;
    }
  }

  protected readonly context = computed<SparkTileContext>(() => ({
    boardId: this.id() ?? '',
    editing: this.editing(),
    reloadToken: this.reloadToken(),
  }));

  constructor() {
    effect(onCleanup => {
      const id = this.id();
      let live = true;
      onCleanup(() => live = false);
      untracked(() => {
        this.board.set(null);
        this.draft.set(null);
        this.loadFailed.set(false);
      });
      if (!id) return;
      this.boards.get(id).then(
        board => { if (live) this.board.set(board); },
        () => { if (live) this.loadFailed.set(true); },
      );
    });
  }

  protected title(tile: SparkBoardTile): string {
    return tile.customTitle || this.lang.resolve(tile.title ?? undefined) || tile.id;
  }

  protected renderer(tile: SparkBoardTile): Type<unknown> | null {
    return this.renderers.get(tile.kind) ?? null;
  }

  protected customize(): void {
    const board = this.board();
    if (!board?.canCustomize) return;
    this.error.set(null);
    this.lastEdit.set(null);
    this.draft.set(startDraft(board));
  }

  protected cancel(): void {
    this.draft.set(null);
    this.lastEdit.set(null);
    this.error.set(null);
  }

  protected onColumnCount(change: { columnCount: number }): void {
    this.columnCount.set(change.columnCount);
  }

  protected onLayoutChange(change: TileLayoutChange): void {
    const draft = this.draft();
    if (!draft) return;
    this.draft.set(commitLayout(draft, change));
    this.lastEdit.set({ columnCount: change.columnCount, staleCounts: change.staleCounts });
  }

  /** "Reset this width": this count derives from the others again; the save leaves it out. */
  protected resetWidth(): void {
    const count = this.columnCount();
    if (count !== null) this.tileManager()?.resetLayout(count);
  }

  protected remove(id: string): void {
    const draft = this.draft();
    if (draft) this.draft.set(removeTile(draft, id));
  }

  protected startRename(tile: SparkBoardTile): void {
    this.renaming.set({ id: tile.id, title: tile.customTitle ?? this.title(tile) });
  }

  protected setRenameTitle(title: string): void {
    const renaming = this.renaming();
    if (renaming) this.renaming.set({ ...renaming, title });
  }

  protected confirmRename(): void {
    const renaming = this.renaming();
    const draft = this.draft();
    if (renaming && draft) {
      const tile = draft.tiles.find(t => t.id === renaming.id);
      // Typing the title the tile already shows is not a rename.
      const title = tile && !tile.customTitle && renaming.title.trim() === this.title(tile) ? '' : renaming.title;
      this.draft.set(renameTile(draft, renaming.id, title));
    }
    this.renaming.set(null);
  }

  protected onPick(pick: SparkBoardPick): void {
    const draft = this.draft();
    if (draft) this.draft.set(addTile(draft, pick));
    this.pickerOpen.set(false);
  }

  protected save(): void {
    if (this.lastEdit()?.staleCounts.length) this.askNarrower.set(true);
    else void this.send(false);
  }

  protected async send(applySmaller: boolean): Promise<void> {
    this.askNarrower.set(false);
    const id = this.id();
    const board = this.board();
    let draft = this.draft();
    if (!id || !board || !draft) return;
    const edit = this.lastEdit();
    if (applySmaller && edit) draft = applyToNarrower(draft, edit.columnCount);
    this.busy.set(true);
    this.error.set(null);
    try {
      this.loaded(await this.boards.save(id, toSaveRequest(draft, board.changeVector)));
    } catch (e) {
      this.error.set(this.lang.t(e instanceof HttpErrorResponse && e.status === 409 ? 'boards.spa.conflict' : 'boards.spa.saveFailed'));
    } finally {
      this.busy.set(false);
    }
  }

  protected async reset(): Promise<void> {
    const id = this.id();
    if (!id || !confirm(this.lang.t('boards.spa.resetConfirm'))) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      this.loaded(await this.boards.reset(id));
    } catch {
      this.error.set(this.lang.t('boards.spa.saveFailed'));
    } finally {
      this.busy.set(false);
    }
  }

  private loaded(board: SparkBoard): void {
    this.board.set(board);
    this.draft.set(null);
    this.lastEdit.set(null);
    this.reloadToken.update(t => t + 1);
  }
}
