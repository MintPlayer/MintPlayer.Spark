import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { SparkGridCellComponent, SparkGridRenderers } from '@mintplayer/ng-spark/grid';
import { QueryCellValuePipe, QueryReferenceChipsPipe, ReferenceChip, ResolveTranslationPipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { cellValue } from '@mintplayer/ng-spark/renderers';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import {
  EntityAttributeDefinition, EntityPermissions, EntityType, LookupReference, PersistentObject, QueryColumn, QueryResultItem,
  SparkDeletedFilter, valueFor,
} from '@mintplayer/ng-spark/models';
import { SparkHistoryService, SparkRevision } from './spark-history.service';
import { diffRevision, revisionAttributes } from './revision-diff';

type HistoryView = 'view' | 'diff';

/** One attribute of one object, ready for `<spark-grid-cell>`. */
interface HistoryCell {
  column: QueryColumn;
  display: unknown;
  rendererValue: unknown;
  chips: ReferenceChip[];
}

const cellValuePipe = new QueryCellValuePipe();
const chipsPipe = new QueryReferenceChipsPipe();

/**
 * A row's revision history (#460, History package): the revision list, a read-only view of one
 * revision, its field diff against the current object, and Revert.
 *
 * Standalone — bind it anywhere — and also what the History detail panel
 * (`provideSparkHistory()`) renders on every routed detail page.
 *
 * Rights come from `permissions`: nothing is listed without `canViewHistory`, and Revert is offered
 * only with `canRevert` (the server's `Revert/T` together with `Edit/T`), never for the current
 * revision, a delete revision, or a row opened from the recycle bin (Revert saves through the live
 * pipeline, which does not see a deleted row). The server enforces all of it again.
 */
@Component({
  selector: 'spark-po-history',
  imports: [DatePipe, BsAlertComponent, BsBadgeComponent, BsSpinnerComponent, BsTableComponent, SparkGridCellComponent, ResolveTranslationPipe, TranslateKeyPipe],
  templateUrl: './spark-po-history.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkPoHistoryComponent {
  private readonly history = inject(SparkHistoryService);
  protected readonly lang = inject(SparkLanguageService);

  /** Entity type id or alias, as the endpoints take it. */
  type = input.required<string>();
  id = input.required<string>();
  entityType = input.required<EntityType>();
  /** The object as it is now — the right-hand side of every diff. */
  current = input<PersistentObject | null>(null);
  permissions = input<EntityPermissions | null>(null);
  /** The mode the row was opened with (`only` = from the recycle bin). */
  deleted = input<SparkDeletedFilter | null>(null);
  /** Rows per request (server default 50, at most 200). */
  pageSize = input(50);

  /** Emitted with the saved object after a successful Revert. */
  reverted = output<PersistentObject>();

  protected readonly colors = Color;
  protected readonly revisions = signal<SparkRevision[]>([]);
  protected readonly loading = signal(false);
  protected readonly hasMore = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly selected = signal<SparkRevision | null>(null);
  protected readonly selectedObject = signal<PersistentObject | null>(null);
  protected readonly selectedLoading = signal(false);
  protected readonly view = signal<HistoryView>('diff');
  protected readonly reverting = signal(false);

  protected readonly canView = computed(() => this.permissions()?.canViewHistory === true);

  protected readonly attributes = computed(() => revisionAttributes(this.entityType()));
  protected readonly changes = computed(() => diffRevision(this.entityType(), this.selectedObject(), this.current()));

  private readonly gridRenderers = inject(SparkGridRenderers);
  /** Lookup-reference labels for the shown attributes, so a lookup value reads as its label. */
  private readonly lookupOptions = signal<Record<string, LookupReference>>({});

  /**
   * The read-only view: every shown attribute of the selected revision, rendered by the same
   * `<spark-grid-cell>` (and custom renderers) as a query grid — a reference as its label, a date in
   * the viewer's culture and zone, a boolean as a checkbox — instead of the raw wire value.
   */
  protected readonly viewCells = computed(() => {
    const revision = this.selectedObject();
    return revision ? this.attributes().map(a => ({ attribute: a, cell: this.cell(a, revision) })) : [];
  });

  /** The diff rows, both sides rendered like {@link viewCells}. */
  protected readonly changeCells = computed(() => {
    const revision = this.selectedObject();
    const current = this.current();
    return this.changes().map(change => ({
      change,
      old: this.cell(change.attribute, revision),
      now: this.cell(change.attribute, current),
    }));
  });

  protected readonly canRevertSelected = computed(() => {
    const revision = this.selected();
    return !!revision
      && this.permissions()?.canRevert === true
      && this.deleted() !== 'only'
      && !revision.isCurrent
      && !revision.isDeleteRevision
      && !!this.selectedObject();
  });

  constructor() {
    effect(() => {
      const attributes = this.attributes();
      untracked(() => {
        this.gridRenderers.loadLookupOptions(attributes).then(o => this.lookupOptions.set(o), () => this.lookupOptions.set({}));
      });
    });

    // (Re)load the list when the row, its mode or the rights change. A revert emits a new `current`
    // but keeps the id, so it is not in this effect; `refresh()` reloads explicitly.
    effect(() => {
      const type = this.type();
      const id = this.id();
      const deleted = this.deleted();
      const allowed = this.canView();
      untracked(() => {
        this.clearSelection();
        this.revisions.set([]);
        if (allowed && type && id) void this.loadPage(type, id, deleted, 0);
      });
    });
  }

  /** Reloads the list from the top, dropping the selection. */
  async refresh(): Promise<void> {
    this.clearSelection();
    this.revisions.set([]);
    await this.loadPage(this.type(), this.id(), this.deleted(), 0);
  }

  async loadMore(): Promise<void> {
    await this.loadPage(this.type(), this.id(), this.deleted(), this.revisions().length);
  }

  private async loadPage(type: string, id: string, deleted: SparkDeletedFilter | null, skip: number): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const take = this.pageSize();
      const page = await this.history.list(type, id, { skip, take, deleted });
      // A response for a row the inputs have moved away from is dropped.
      if (type !== this.type() || id !== this.id()) return;
      this.revisions.update(existing => skip === 0 ? page : [...existing, ...page]);
      this.hasMore.set(page.length >= take);
    } catch (e) {
      this.error.set(this.describe(e as HttpErrorResponse));
    } finally {
      this.loading.set(false);
    }
  }

  async select(revision: SparkRevision): Promise<void> {
    if (this.selected()?.changeVector === revision.changeVector) {
      this.clearSelection();
      return;
    }
    this.selected.set(revision);
    this.selectedObject.set(null);
    this.error.set(null);
    this.selectedLoading.set(true);
    try {
      const po = await this.history.get(this.type(), this.id(), revision.changeVector, { deleted: this.deleted() });
      if (this.selected()?.changeVector === revision.changeVector) this.selectedObject.set(po);
    } catch (e) {
      this.error.set(this.describe(e as HttpErrorResponse));
    } finally {
      this.selectedLoading.set(false);
    }
  }

  clearSelection(): void {
    this.selected.set(null);
    this.selectedObject.set(null);
  }

  async revert(): Promise<void> {
    const revision = this.selected();
    if (!revision || !this.canRevertSelected() || this.reverting()) return;
    if (!confirm(this.lang.t('history.confirmRevert'))) return;

    this.reverting.set(true);
    this.error.set(null);
    try {
      const saved = await this.history.revert(this.type(), this.id(), revision.changeVector);
      this.reverted.emit(saved);
      await this.refresh();
    } catch (e) {
      this.error.set(this.describe(e as HttpErrorResponse));
    } finally {
      this.reverting.set(false);
    }
  }

  /**
   * One attribute of `po` for `<spark-grid-cell>`. A persistent object is a row the grid pipes read
   * (`valueFor` understands both shapes), so the value resolution is the grid's, not a copy of it.
   */
  private cell(attribute: EntityAttributeDefinition, po: PersistentObject | null): HistoryCell {
    const column = attribute as unknown as QueryColumn;
    const row = po as unknown as QueryResultItem | null;
    const display = cellValuePipe.transform(column, row, this.lookupOptions());
    return {
      column,
      // The detail page's placeholder for an empty value; a boolean keeps null (an indeterminate box).
      display: attribute.dataType !== 'boolean' && (display == null || display === '') ? '-' : display,
      rendererValue: cellValue(valueFor(row, attribute.name)),
      chips: chipsPipe.transform(column, row),
    };
  }

  protected who(revision: SparkRevision): string {
    return revision.userName || revision.userId || this.lang.t('history.unknownUser');
  }

  private describe(err: HttpErrorResponse): string {
    switch (err?.status) {
      case 404: return this.lang.t('history.unavailable');
      case 409: return this.lang.t('history.conflict');
      default: return err?.error?.error || err?.message || this.lang.t('common.actionFailed');
    }
  }
}
