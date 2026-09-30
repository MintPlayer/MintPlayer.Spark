import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
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
  QueryColumn,
  QueryResultItem,
  formValuesEqual,
  fromDateInputValue,
  isDateDataType,
  isReservedAsDetailKey,
  valueFor,
} from '@mintplayer/ng-spark/models';
import { ConflictSide, MergeConflict } from './conflict-merge';
import { ReferenceLabels } from './reference-labels';

/**
 * One side of a conflict: ready for `<spark-grid-cell>`, or, for a whole row or embedded object, its
 * attributes cell by cell (`fields`), or plain text (a removed row, or a value with no type to show).
 */
interface ConflictCell {
  column: QueryColumn;
  display: unknown;
  rendererValue: unknown;
  chips: ReferenceChip[];
  item: PersistentObject;
  text?: string;
  fields?: ConflictField[];
}

/** One attribute of a row on one side, and whether the other side holds something else there. */
interface ConflictField {
  attribute: EntityAttributeDefinition;
  cell: ConflictCell;
  differs: boolean;
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
  imports: [NgTemplateOutlet, BsModalHostComponent, BsModalDirective, BsModalHeaderDirective, BsModalBodyDirective, BsModalFooterDirective, BsButtonTypeDirective, SparkGridCellComponent, ResolveTranslationPipe, TranslateKeyPipe],
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
                      <td class="spark-conflict-mine-value">
                        <label class="d-flex gap-2 align-items-start">
                          <input type="radio" class="form-check-input spark-conflict-mine" [name]="entry.conflict.path"
                                 [checked]="choices()[entry.conflict.path] === 'mine'" (change)="choose(entry.conflict.path, 'mine')" />
                          <ng-container [ngTemplateOutlet]="side" [ngTemplateOutletContext]="{ $implicit: entry.mine }" />
                        </label>
                      </td>
                      <td class="spark-conflict-theirs-value">
                        <label class="d-flex gap-2 align-items-start">
                          <input type="radio" class="form-check-input spark-conflict-theirs" [name]="entry.conflict.path"
                                 [checked]="choices()[entry.conflict.path] === 'theirs'" (change)="choose(entry.conflict.path, 'theirs')" />
                          <ng-container [ngTemplateOutlet]="side" [ngTemplateOutletContext]="{ $implicit: entry.theirs }" />
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

    <!-- One side's value. A row is its attributes cell by cell, the ones the other side differs in marked. -->
    <ng-template #side let-cell>
      @if (cell.fields) {
        <table class="table table-sm table-borderless mb-0 spark-conflict-row-cells">
          <tbody>
            @for (field of cell.fields; track field.attribute.name) {
              <tr [class.spark-conflict-differs]="field.differs" [attr.data-attribute]="field.attribute.name">
                <th scope="row" class="fw-normal text-muted pe-2">{{ field.attribute.label | resolveTranslation:field.attribute.name }}</th>
                <td [class.fw-bold]="field.differs">
                  <ng-container [ngTemplateOutlet]="value" [ngTemplateOutletContext]="{ $implicit: field.cell }" />
                </td>
              </tr>
            }
          </tbody>
        </table>
      } @else {
        <span><ng-container [ngTemplateOutlet]="value" [ngTemplateOutletContext]="{ $implicit: cell }" /></span>
      }
    </ng-template>
    <ng-template #value let-cell>
      @if (cell.text !== undefined) { {{ cell.text }} } @else {
        <spark-grid-cell [column]="cell.column" [display]="cell.display" [rendererValue]="cell.rendererValue"
                         [item]="cell.item" [chips]="cell.chips" />
      }
    </ng-template>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkPoConflictDialogComponent {
  private readonly language = inject(SparkLanguageService);
  private readonly gridRenderers = inject(SparkGridRenderers);

  /** The true conflicts; the dialog is open while there are any. */
  conflicts = input<MergeConflict[]>([]);
  /**
   * Reference labels by id — the form keeps only ids. Built by the edit page from the object as
   * loaded, the re-fetched one, and the form's reference candidates (see `reference-labels.ts`).
   * An id without a label shows as the id.
   */
  referenceLabels = input<ReferenceLabels>({});
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
    const labels = this.referenceLabels();
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
      group.entries.push({
        conflict,
        mine: this.sideCell(conflict, conflict.mine, conflict.theirs, labels, lookups),
        theirs: this.sideCell(conflict, conflict.theirs, conflict.mine, labels, lookups),
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
        const attributes = conflicts.flatMap(c => [...(c.kind === 'value' ? [c.attribute] : []), ...(c.rowAttributes ?? [])]);
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

  /**
   * One side of a conflict. A whole row (or an embedded object, when its type resolved) is shown
   * attribute by attribute with the same cells as a scalar, marking where `other` — the other side —
   * holds something else; a row absent on this side is "(removed)", which is what choosing it means.
   */
  private sideCell(conflict: MergeConflict, value: unknown, other: unknown, labels: ReferenceLabels, lookups: Record<string, LookupReference>): ConflictCell {
    const rowShaped = conflict.kind === 'row' || conflict.attribute.dataType === 'AsDetail';
    if (!rowShaped) return this.cell(conflict.attribute, value, labels, lookups);

    const cell = this.cell(conflict.attribute, undefined, labels, lookups);
    if (value === undefined || value === null) {
      cell.text = value === undefined && conflict.kind === 'row' ? this.language.t('common.conflictRowRemoved') : '-';
      return cell;
    }
    if (!conflict.rowAttributes || typeof value !== 'object' || Array.isArray(value)) {
      cell.text = summarize(value) || '-';
      return cell;
    }
    const row = value as Record<string, unknown>;
    const otherRow = typeof other === 'object' && other !== null && !Array.isArray(other) ? other as Record<string, unknown> : undefined;
    cell.fields = conflict.rowAttributes
      .filter(a => a.isVisible !== false)
      .sort((a, b) => (a.order ?? 0) - (b.order ?? 0))
      .map(attribute => ({
        attribute,
        cell: this.cell(attribute, row[attribute.name], labels, lookups),
        differs: otherRow !== undefined && !formValuesEqual(row[attribute.name], otherRow[attribute.name]),
      }));
    return cell;
  }

  /** One attribute's value, in the form's shape, as a `<spark-grid-cell>`. */
  private cell(attribute: EntityAttributeDefinition, value: unknown, labels: ReferenceLabels, lookups: Record<string, LookupReference>): ConflictCell {
    const column = attribute as unknown as QueryColumn;
    const wire = isDateDataType(attribute.dataType) ? fromDateInputValue(attribute.dataType, value) : value;
    // The form keeps a reference's id, not its label; the label comes from the map by id.
    const isReference = attribute.dataType === 'Reference';
    const breadcrumb = isReference && typeof wire === 'string' ? labels[wire] : undefined;
    const breadcrumbs = isReference && Array.isArray(wire)
      ? Object.fromEntries(wire.filter((id): id is string => typeof id === 'string' && id in labels).map(id => [id, labels[id]]))
      : undefined;
    const item: PersistentObject = {
      id: '', name: '', objectTypeId: '',
      attributes: [{
        id: attribute.id, name: attribute.name, dataType: attribute.dataType, isArray: attribute.isArray,
        isRequired: false, isVisible: true, isReadOnly: true, order: 0, rules: [],
        value: wire, breadcrumb, breadcrumbs,
      }],
    };
    const cell: ConflictCell = { column, display: '', rendererValue: null, chips: [], item };

    if (value === undefined && attribute.dataType === 'AsDetail') return cell;
    if (attribute.dataType === 'AsDetail' || (typeof value === 'object' && value !== null && !Array.isArray(value))) {
      // A nested embedded object inside a row: one line, which is as deep as the dialog goes.
      cell.text = summarize(value) || '-';
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
