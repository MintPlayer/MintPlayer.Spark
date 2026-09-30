import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsModalHostComponent, BsModalDirective, BsModalHeaderDirective, BsModalBodyDirective, BsModalFooterDirective } from '@mintplayer/ng-bootstrap/modal';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { SparkGridCellComponent, SparkGridRenderers } from '@mintplayer/ng-spark/grid';
import { QueryCellValuePipe, QueryReferenceChipsPipe, ReferenceChip, ResolveTranslationPipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { cellValue } from '@mintplayer/ng-spark/renderers';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import {
  EntityAttributeDefinition,
  LookupReference,
  PersistentObject,
  PersistentObjectAttribute,
  QueryColumn,
  QueryResultItem,
  formValuesEqual,
  fromDateInputValue,
  isDateDataType,
  isReservedAsDetailKey,
  valueFor,
} from '@mintplayer/ng-spark/models';
import { ConflictSide, MergeConflict } from './conflict-merge';

/** One side of a conflict, ready for `<spark-grid-cell>`, or as plain text for a whole row. */
interface ConflictCell {
  column: QueryColumn;
  display: unknown;
  rendererValue: unknown;
  chips: ReferenceChip[];
  item: PersistentObject;
  text?: string;
}

interface ConflictGroup {
  key: string;
  /** The root attribute and row this group sits in; empty for plain top-level attributes. */
  attribute?: EntityAttributeDefinition;
  rowLabel?: string;
  entries: { conflict: MergeConflict; mine: ConflictCell; theirs: ConflictCell }[];
}

const cellValuePipe = new QueryCellValuePipe();
const chipsPipe = new QueryReferenceChipsPipe();

/**
 * The true conflicts of a three-way merge after a 409, Mine or Theirs per conflict (grouped by
 * AsDetail row), rendered with the same `<spark-grid-cell>` a query grid and History use. It only
 * collects choices: applying them, and rebasing onto the fresh etag, is the edit page's job — and
 * nothing is saved from here.
 */
@Component({
  selector: 'spark-po-conflict-dialog',
  imports: [BsModalHostComponent, BsModalDirective, BsModalHeaderDirective, BsModalBodyDirective, BsModalFooterDirective, BsButtonTypeDirective, SparkGridCellComponent, ResolveTranslationPipe, TranslateKeyPipe],
  template: `
    <bs-modal [isOpen]="conflicts().length > 0" (isOpenChange)="!$event && cancelled.emit()">
      <div *bsModal>
        <div bsModalHeader>
          <h5 class="modal-title">{{ 'common.conflictTitle' | t }}</h5>
        </div>
        <div bsModalBody class="spark-conflict-dialog">
          <p>{{ 'common.conflictIntro' | t }}</p>
          @if (audit()) {
            <p class="text-muted spark-conflict-audit">{{ audit() }}</p>
          }
          @if (theyChanged()) {
            <p class="text-muted spark-conflict-they-changed">{{ theyChanged() }}</p>
          }
          @for (group of groups(); track group.key) {
            <div class="mb-3 spark-conflict-group">
              @if (group.attribute) {
                <h6>
                  <span>{{ group.attribute.label | resolveTranslation:group.attribute.name }}</span>
                  @if (group.rowLabel) {
                    <span> — {{ group.rowLabel }}</span>
                  }
                </h6>
              }
              <table class="table table-sm align-middle mb-0">
                <thead>
                  <tr>
                    <th>{{ 'common.conflictField' | t }}</th>
                    <th>{{ 'common.conflictMine' | t }}</th>
                    <th>{{ 'common.conflictTheirs' | t }}</th>
                  </tr>
                </thead>
                <tbody>
                  @for (entry of group.entries; track entry.conflict.path) {
                    <tr class="spark-conflict" [attr.data-path]="entry.conflict.path">
                      <th scope="row">
                        @if (entry.conflict.kind === 'row') {
                          {{ entry.conflict.rowLabel }}
                        } @else {
                          {{ entry.conflict.attribute.label | resolveTranslation:entry.conflict.attribute.name }}
                        }
                      </th>
                      <td>
                        <label class="d-flex gap-2 align-items-start">
                          <input type="radio" class="form-check-input spark-conflict-mine" [name]="entry.conflict.path"
                                 [checked]="choices()[entry.conflict.path] === 'mine'" (change)="choose(entry.conflict.path, 'mine')" />
                          <span>
                            @if (entry.mine.text !== undefined) { {{ entry.mine.text }} } @else {
                              <spark-grid-cell [column]="entry.mine.column" [display]="entry.mine.display" [rendererValue]="entry.mine.rendererValue"
                                               [item]="entry.mine.item" [chips]="entry.mine.chips" />
                            }
                          </span>
                        </label>
                      </td>
                      <td>
                        <label class="d-flex gap-2 align-items-start">
                          <input type="radio" class="form-check-input spark-conflict-theirs" [name]="entry.conflict.path"
                                 [checked]="choices()[entry.conflict.path] === 'theirs'" (change)="choose(entry.conflict.path, 'theirs')" />
                          <span>
                            @if (entry.theirs.text !== undefined) { {{ entry.theirs.text }} } @else {
                              <spark-grid-cell [column]="entry.theirs.column" [display]="entry.theirs.display" [rendererValue]="entry.theirs.rendererValue"
                                               [item]="entry.theirs.item" [chips]="entry.theirs.chips" />
                            }
                          </span>
                        </label>
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        </div>
        <div bsModalFooter>
          <button type="button" class="spark-conflict-all-mine" [color]="colors.secondary" (click)="chooseAll('mine')">{{ 'common.conflictKeepAllMine' | t }}</button>
          <button type="button" class="spark-conflict-all-theirs" [color]="colors.secondary" (click)="chooseAll('theirs')">{{ 'common.conflictTakeAllTheirs' | t }}</button>
          <button type="button" class="spark-conflict-cancel" [color]="colors.secondary" (click)="cancelled.emit()">{{ 'common.cancel' | t }}</button>
          <button type="button" class="spark-conflict-apply" [color]="colors.primary" [disabled]="!complete()" (click)="apply()">{{ 'common.conflictApply' | t }}</button>
        </div>
      </div>
    </bs-modal>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkPoConflictDialogComponent {
  private readonly language = inject(SparkLanguageService);
  private readonly gridRenderers = inject(SparkGridRenderers);

  /** The true conflicts; the dialog is open while there are any. */
  conflicts = input<MergeConflict[]>([]);
  /** The object as loaded and as re-fetched: sources of the reference labels the form does not keep. */
  base = input<PersistentObject | null>(null);
  theirs = input<PersistentObject | null>(null);
  /** "Changed by X at T", when the object is audited. */
  audit = input<string | null>(null);
  /** "They also changed: …", the changes merged without a conflict. */
  theyChanged = input<string | null>(null);

  resolved = output<Record<string, ConflictSide>>();
  cancelled = output<void>();

  colors = Color;
  protected readonly choices = signal<Record<string, ConflictSide>>({});
  private readonly lookupOptions = signal<Record<string, LookupReference>>({});

  protected readonly complete = computed(() => {
    const choices = this.choices();
    return this.conflicts().every(c => choices[c.path] !== undefined);
  });

  protected readonly groups = computed<ConflictGroup[]>(() => {
    const lookups = this.lookupOptions();
    const sources = [this.theirs(), this.base()].filter((p): p is PersistentObject => !!p).flatMap(p => p.attributes ?? []);
    const groups = new Map<string, ConflictGroup>();
    for (const conflict of this.conflicts()) {
      // A row conflict sits in its row's group, so it heads the same block as its attribute conflicts.
      const key = conflict.kind === 'row' ? conflict.path : conflict.group;
      let group = groups.get(key);
      if (!group) {
        group = key === ''
          ? { key, entries: [] }
          : { key, attribute: conflict.rootAttribute, rowLabel: conflict.kind === 'row' ? undefined : conflict.rowLabel, entries: [] };
        groups.set(key, group);
      }
      const rootLevel = conflict.group === '';
      group.entries.push({
        conflict,
        mine: this.cell(conflict, conflict.mine, rootLevel ? sources : [], lookups),
        theirs: this.cell(conflict, conflict.theirs, rootLevel ? sources : [], lookups),
      });
    }
    return [...groups.values()];
  });

  constructor() {
    // A new set of conflicts starts with nothing chosen, and loads the lookup labels it shows.
    effect(() => {
      const conflicts = this.conflicts();
      untracked(() => {
        this.choices.set({});
        const attributes = conflicts.filter(c => c.kind === 'value').map(c => c.attribute);
        this.gridRenderers.loadLookupOptions(attributes).then(o => this.lookupOptions.set(o), () => this.lookupOptions.set({}));
      });
    });
  }

  choose(path: string, side: ConflictSide): void {
    this.choices.update(c => ({ ...c, [path]: side }));
  }

  chooseAll(side: ConflictSide): void {
    const all: Record<string, ConflictSide> = {};
    for (const c of this.conflicts()) all[c.path] = side;
    this.choices.set(all);
  }

  apply(): void {
    if (!this.complete()) return;
    this.resolved.emit(this.choices());
  }

  private cell(conflict: MergeConflict, value: unknown, sources: PersistentObjectAttribute[], lookups: Record<string, LookupReference>): ConflictCell {
    const attribute = conflict.attribute;
    const column = attribute as unknown as QueryColumn;
    const wire = isDateDataType(attribute.dataType) ? fromDateInputValue(attribute.dataType, value) : value;
    // The form keeps a reference's id, not its label; the label is on whichever read holds that id.
    const source = sources.find(a => a.name === attribute.name && formValuesEqual(a.value, wire));
    const item: PersistentObject = {
      id: '', name: '', objectTypeId: '',
      attributes: [{
        id: attribute.id, name: attribute.name, dataType: attribute.dataType, isArray: attribute.isArray,
        isRequired: false, isVisible: true, isReadOnly: true, order: 0, rules: [],
        value: wire, breadcrumb: source?.breadcrumb, breadcrumbs: source?.breadcrumbs,
      }],
    };
    const cell: ConflictCell = { column, display: '', rendererValue: null, chips: [], item };

    if (conflict.kind === 'row' || attribute.dataType === 'AsDetail' || (typeof value === 'object' && value !== null && !Array.isArray(value))) {
      cell.text = value === undefined ? this.language.t('common.conflictRowRemoved') : summarize(value) || '-';
      return cell;
    }

    const row = item as unknown as QueryResultItem;
    const display = cellValuePipe.transform(column, row, lookups);
    cell.display = attribute.dataType !== 'boolean' && (display == null || display === '') ? '-' : display;
    cell.rendererValue = cellValue(valueFor(row, attribute.name));
    cell.chips = chipsPipe.transform(column, row);
    return cell;
  }
}

/** A flattened row as one line of text: its values, without the per-read reserved keys. */
function summarize(value: unknown): string {
  if (value === null || value === undefined) return '';
  if (Array.isArray(value)) return value.map(summarize).filter(s => s !== '').join('; ');
  if (typeof value === 'object') {
    return Object.entries(value as Record<string, unknown>)
      .filter(([key]) => !isReservedAsDetailKey(key))
      .map(([, v]) => summarize(v))
      .filter(s => s !== '')
      .join(', ');
  }
  return String(value);
}
