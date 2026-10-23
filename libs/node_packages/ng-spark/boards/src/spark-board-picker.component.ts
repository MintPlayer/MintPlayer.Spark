import { ChangeDetectionStrategy, Component, computed, effect, inject, input, model, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsButtonGroupComponent } from '@mintplayer/ng-bootstrap/button-group';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsListGroupComponent, BsListGroupItemComponent } from '@mintplayer/ng-bootstrap/list-group';
import { BsModalBodyDirective, BsModalDirective, BsModalFooterDirective, BsModalHeaderDirective, BsModalHostComponent } from '@mintplayer/ng-bootstrap/modal';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SparkBoardCatalogEntry, SparkBoardTile } from './spark-board.models';
import { SparkBoardsService } from './spark-boards.service';

/** What Add tile hands the board: a removed default to restore (`id`), or a new tile. */
export interface SparkBoardPick {
  id?: string;
  kind: string;
  target: Record<string, string>;
  title?: SparkBoardTile['title'];
  defaultSize?: SparkBoardTile['defaultSize'];
}

/**
 * **Add tile** (#530): the caller's catalog (`/spark/boards/{board}/catalog`, already filtered by
 * rights) and the defaults they removed. A target that can be shown several ways (a query as a grid
 * or as a count) offers one button per kind.
 */
@Component({
  selector: 'spark-board-picker',
  imports: [
    FormsModule, BsButtonGroupComponent, BsButtonTypeDirective, BsFormComponent, BsFormControlDirective, BsListGroupComponent, BsListGroupItemComponent,
    BsModalHostComponent, BsModalDirective, BsModalHeaderDirective, BsModalBodyDirective, BsModalFooterDirective,
    SparkIconComponent, TranslateKeyPipe,
  ],
  styleUrl: './spark-board-picker.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <bs-modal [isOpen]="isOpen()" (isOpenChange)="isOpen.set($event)">
      <div *bsModal>
        <div bsModalHeader><h5 class="modal-title">{{ 'boards.spa.pickerTitle' | t }}</h5></div>
        <div bsModalBody>
          <bs-form>
            <input type="search" class="spark-picker-search" [attr.aria-label]="'boards.spa.pickerSearch' | t"
                   [placeholder]="'boards.spa.pickerSearch' | t" [ngModel]="search()" (ngModelChange)="search.set($event)" />
          </bs-form>
          @if (failed()) {
            <p class="spark-picker-error" role="alert">{{ 'boards.spa.loadFailed' | t }}</p>
          }
          @for (group of groups(); track group.key) {
            <h6 class="spark-picker-group">{{ group.label | t }}</h6>
            <bs-list-group>
              @for (row of group.rows; track row.key) {
                <bs-list-group-item>
                  <span class="spark-picker-row">
                    @if (row.icon) { <spark-icon [name]="row.icon" /> }
                    <span class="spark-picker-name">{{ row.name }}</span>
                    <bs-button-group class="spark-picker-kinds">
                      @for (kind of row.kinds; track kind) {
                        <button type="button" class="spark-picker-add" [attr.data-kind]="kind" [color]="colors.primary" (click)="pick(row, kind)">
                          {{ kindLabel(kind) }}
                        </button>
                      }
                    </bs-button-group>
                  </span>
                </bs-list-group-item>
              }
            </bs-list-group>
          }
        </div>
        <div bsModalFooter>
          <button type="button" [color]="colors.secondary" (click)="isOpen.set(false)">{{ 'boards.spa.cancel' | t }}</button>
        </div>
      </div>
    </bs-modal>
  `,
})
export class SparkBoardPickerComponent {
  private readonly boards = inject(SparkBoardsService);
  private readonly lang = inject(SparkLanguageService);

  readonly isOpen = model(false);
  readonly boardId = input.required<string>();
  readonly removed = input<readonly SparkBoardTile[]>([]);
  readonly picked = output<SparkBoardPick>();

  protected readonly colors = Color;
  protected readonly search = signal('');
  protected readonly failed = signal(false);
  private readonly entries = signal<SparkBoardCatalogEntry[]>([]);

  protected readonly groups = computed(() => {
    const term = this.search().trim().toLowerCase();
    const matches = (name: string) => !term || name.toLowerCase().includes(term);
    const removed = this.removed().map(t => ({
      key: `removed:${t.id}`, id: t.id, name: this.lang.resolve(t.title ?? undefined) || t.id, icon: null as string | null,
      kinds: [t.kind], target: t.target, title: t.title, defaultSize: t.defaultSize,
    }));
    const catalog = this.entries().map((e, i) => ({
      key: `${e.group}:${i}`, id: undefined as string | undefined, name: this.lang.resolve(e.title ?? undefined) || Object.values(e.target).join(' '),
      icon: e.icon ?? null, kinds: e.kinds, target: e.target, title: e.title, defaultSize: undefined, group: e.group,
    }));
    return [
      { key: 'removed', label: 'boards.spa.pickerRemoved', rows: removed.filter(r => matches(r.name)) },
      { key: 'programUnits', label: 'boards.spa.pickerMenu', rows: catalog.filter(r => r.group === 'programUnits' && matches(r.name)) },
      { key: 'queries', label: 'boards.spa.pickerQueries', rows: catalog.filter(r => r.group !== 'programUnits' && matches(r.name)) },
    ].filter(g => g.rows.length);
  });

  constructor() {
    effect(onCleanup => {
      if (!this.isOpen()) return;
      const id = this.boardId();
      let live = true;
      onCleanup(() => live = false);
      this.failed.set(false);
      this.boards.catalog(id).then(
        catalog => { if (live) this.entries.set(catalog.entries); },
        () => { if (live) this.failed.set(true); },
      );
    });
  }

  protected kindLabel(kind: string): string {
    return this.lang.t(`boards.spa.kind_${kind}`, kind);
  }

  protected pick(row: { id?: string; target: Record<string, string>; title?: SparkBoardTile['title']; defaultSize?: SparkBoardTile['defaultSize'] }, kind: string): void {
    this.picked.emit({ id: row.id, kind, target: { ...row.target }, title: row.title, defaultSize: row.defaultSize });
  }
}
