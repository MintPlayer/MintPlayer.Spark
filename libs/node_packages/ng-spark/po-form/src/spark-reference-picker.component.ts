import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal, viewChild } from '@angular/core';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsInputGroupComponent } from '@mintplayer/ng-bootstrap/input-group';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { BsModalHostComponent, BsModalDirective, BsModalHeaderDirective, BsModalBodyDirective, BsModalFooterDirective } from '@mintplayer/ng-bootstrap/modal';
import { BsDatatableComponent, BsDatatableColumnDirective, BsRowTemplateDirective, DatatableSettings, type BsDatatableFetch, type BsDatatableFetchRequest } from '@mintplayer/ng-bootstrap/datatable';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { PaginationResponse } from '@mintplayer/pagination';
import { SparkService, SparkLanguageService, sparkDatatableLabels } from '@mintplayer/ng-spark/services';
import { EntityType, QueryColumn, QueryResultItem } from '@mintplayer/ng-spark/models';
import { TranslateKeyPipe, ResolveTranslationPipe, ReferenceAttrValuePipe, LoadedReference, referenceLabel } from '@mintplayer/ng-spark/pipes';
import { SparkAttributeDescriptionComponent } from '@mintplayer/ng-spark/attribute-description';
import { SparkSearchBoxComponent } from '@mintplayer/ng-spark/grid';

/** The parent a reference query runs under, as the form's option queries pass it. */
export interface ReferenceQueryParent {
  parentId?: string;
  parentType?: string;
}

/**
 * Reusable Reference value picker: a readonly textbox showing the current selection's
 * breadcrumb/name plus a "…" button that opens a searchable modal grid over the candidates.
 * Per-instance state (no single top-level slot), so it works for top-level reference attributes
 * and for individual inline AsDetail reference cells alike.
 *
 * Given the reference `query`, the modal pages, sorts and searches **on the server**: every page,
 * page size, sort and (debounced) search is one `executeQueryByName` with `skip`/`take`/`search`, and
 * the pager is driven by the result's `totalItems` — the same `[fetch]` loop the query grid runs. It
 * used to page and search the `options` the parent pre-loaded, which are one page of the query (50
 * by default), so a candidate past the 50th could be neither found nor paged to and nothing said the
 * list was cut (`docs/default_index_PRD.md`, the note beside D10). Row security applies unchanged:
 * it is the same query endpoint.
 *
 * Without a `query` (a refresh hook replaced the option list, so the hook's list is the whole set)
 * the modal pages and searches `options` in memory, as before. `options` also still labels the
 * textbox after a change; see `displayValue`.
 */
@Component({
  selector: 'spark-reference-picker',
  imports: [BsFormControlDirective, BsInputGroupComponent, BsButtonTypeDirective, BsModalHostComponent, BsModalDirective, BsModalHeaderDirective, BsModalBodyDirective, BsModalFooterDirective, BsDatatableComponent, BsDatatableColumnDirective, BsRowTemplateDirective, BsSpinnerComponent, TranslateKeyPipe, ResolveTranslationPipe, ReferenceAttrValuePipe, SparkAttributeDescriptionComponent, SparkSearchBoxComponent],
  templateUrl: './spark-reference-picker.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class SparkReferencePickerComponent {
  private readonly sparkService = inject(SparkService);
  private readonly lang = inject(SparkLanguageService);

  /** Currently selected referenced id (or null when unset). */
  value = input<string | null>(null);
  /**
   * Candidate rows the parent holds: the query's first page, or a refresh hook's replacement list.
   * Searched and paged in memory only when no `query` is given.
   */
  options = input<QueryResultItem[]>([]);
  /** The reference query's name. Set, the modal pages and searches on the server. */
  query = input<string | null | undefined>(undefined);
  /** The parent the reference query runs under (`parentId`/`parentType`), as for the pre-loaded options. */
  queryParent = input<ReferenceQueryParent | null | undefined>(undefined);
  /**
   * Target entity type's CLR type name. The modal shows its grid once this type has loaded; the
   * columns themselves come from the query result (#284, D9).
   */
  referenceType = input<string | undefined>(undefined);
  /** Renders the field with the Bootstrap is-invalid state. */
  isInvalid = input(false);
  /** Optional id forwarded to the readonly input (for label association). */
  inputId = input<string | undefined>(undefined);
  /** Optional resolved label appended to the modal header ("Select {title}"). */
  title = input<string>('');
  /**
   * The id the attribute held when the object was loaded and its server-resolved label
   * (`PersistentObjectAttribute.breadcrumb`); unset on a create.
   */
  loaded = input<LoadedReference | null | undefined>(undefined);

  valueChange = output<string | null>();
  /**
   * The row that was picked, emitted just before `valueChange`. A row from a server page need not be
   * among the parent's `options`, so the parent keeps it to label the value everywhere it shows it.
   */
  rowPicked = output<QueryResultItem>();

  colors = Color;

  showModal = signal(false);
  entityType = signal<EntityType | null>(null);
  pagination = signal<PaginationResponse<QueryResultItem> | undefined>(undefined);
  settings = signal(SparkReferencePickerComponent.freshSettings());
  /** The published (debounced) search term. */
  searchTerm = signal('');
  /** Rows matching the search, as the last page reported them; shown beside a search. */
  resultCount = signal<number | null>(null);
  /** Set when the last server page failed, so a failure does not read as "no rows". */
  loadError = signal<string | null>(null);
  /** The datatable's `[fetch]` in server mode; null pages `options` in memory. */
  fetchFn = signal<BsDatatableFetch<QueryResultItem> | null>(null);

  private readonly datatable = viewChild(BsDatatableComponent);
  /** The last row picked here, so the textbox can label it even when the parent does not keep it. */
  private readonly picked = signal<QueryResultItem | null>(null);

  /**
   * The columns the executed query result declares, in the order it declares them (#284, D9). The
   * server decides them (surface, rights, the query's `columnOrder`), so the picker draws exactly what
   * the query grid draws for the same query. Empty until the first page arrives, and without a
   * `query`, where the rows carry only a breadcrumb and the template draws that instead.
   */
  columns = signal<QueryColumn[]>([]);

  /** The resize handle and its options dialog, translated; columns are named by `bsDatatableColumnLabel`. */
  datatableLabels = computed(() => sparkDatatableLabels(this.lang));

  // Typed rows so the datatable generic infers the row type. Ignored by the datatable in server mode.
  rows = computed<QueryResultItem[]>(() => this.pagination()?.data ?? []);

  /**
   * The textbox shows the server's label while the value is the loaded one, the chosen row's text
   * after a change, and the id as a last resort (D10, see `referenceLabel`). The options are only the
   * query's first page, so the row picked from a later server page is offered too.
   */
  displayValue = computed(() => {
    const id = this.value();
    if (!id) return this.lang.t('common.notSelected');
    const picked = this.picked();
    return referenceLabel(id, this.loaded(), picked ? [picked, ...this.options()] : this.options());
  });

  private static freshSettings(): DatatableSettings {
    return new DatatableSettings({
      perPage: { values: [10, 25, 50], selected: 10 },
      page: { values: [1], selected: 1 },
      sortColumns: []
    });
  }

  async open(): Promise<void> {
    this.searchTerm.set('');
    this.resultCount.set(null);
    this.loadError.set(null);
    this.settings.set(SparkReferencePickerComponent.freshSettings());

    // Lazily resolve the target entity type; the modal shows its grid once it has.
    const refType = this.referenceType();
    if (!this.entityType() && refType) {
      const types = await this.sparkService.getEntityTypes();
      this.entityType.set(types.find(t => t.clrType === refType) || null);
    }

    // A fresh closure per open: the datatable then fetches page 1 even if the last open ended there.
    const query = this.query();
    this.fetchFn.set(query ? this.makeFetch(query) : null);
    if (!query) this.applyFilter();
    this.showModal.set(true);
  }

  /** The search box publishes its term (debounced while typing, at once on clear). */
  onSearchChange(term: string): void {
    this.searchTerm.set(term ?? '');

    if (!this.fetchFn()) {
      this.settings().page.selected = 1;
      this.applyFilter();
      return;
    }

    // Back to page 1 and a forced re-fetch, as the query grid does for a filter: the datatable
    // dedupes reloads by (page, perPage, sort), none of which a search changes, and `resetPage`
    // makes it fetch page 1 itself rather than wait for the settings binding.
    const s = this.settings();
    this.settings.set(new DatatableSettings({
      perPage: { values: s.perPage.values, selected: s.perPage.selected },
      page: { values: [1], selected: 1 },
      sortColumns: s.sortColumns,
    }));
    const datatable = this.datatable();
    if (datatable) datatable.reload({ resetPage: true });
    else this.fetchFn.set(this.makeFetch(this.query()!));
  }

  /**
   * One server page as the datatable takes it. The term and the parent are read at call time, so a
   * reload after a search sends the new term.
   */
  private makeFetch(query: string): BsDatatableFetch<QueryResultItem> {
    return async (req: BsDatatableFetchRequest) => {
      const parent = this.queryParent();
      try {
        const result = await this.sparkService.executeQueryByName(query, {
          parentId: parent?.parentId,
          parentType: parent?.parentType,
          sortColumns: req.sortColumns,
          skip: (req.page - 1) * req.perPage,
          take: req.perPage,
          search: this.searchTerm().trim() || undefined,
        });
        this.loadError.set(null);
        this.resultCount.set(result.totalItems);
        this.setColumns(result.columns ?? []);
        return {
          data: result.items ?? [],
          totalRecords: result.totalItems,
          totalPages: Math.ceil(result.totalItems / req.perPage) || 1,
          perPage: req.perPage,
          page: req.page,
        };
      } catch {
        // Said out loud: an empty page alone is indistinguishable from a query with no rows.
        this.loadError.set(this.lang.t('spark.query.unavailable', 'This list is not available.'));
        this.resultCount.set(0);
        return { data: [], totalRecords: 0, totalPages: 1, perPage: req.perPage, page: req.page };
      }
    };
  }

  /**
   * Takes a page's columns. Every page of the same query declares the same columns, so an equal set is
   * not re-set: re-rendering every header per page would gain nothing (the query grid does the same).
   */
  private setColumns(columns: QueryColumn[]): void {
    const current = this.columns();
    if (current.length === columns.length && JSON.stringify(current) === JSON.stringify(columns)) return;
    this.columns.set(columns);
  }

  /** In-memory paging and search over `options`, for a picker without a `query`. */
  applyFilter(): void {
    let filteredItems = this.options();

    const term = this.searchTerm().toLowerCase().trim();
    if (term) {
      filteredItems = this.options().filter(item => {
        if (item.breadcrumb?.toLowerCase().includes(term)) return true;
        return item.values.some(v => {
          const value = v.breadcrumb ?? v.value;
          if (value == null) return false;
          return String(value).toLowerCase().includes(term);
        });
      });
    }

    const totalPages = Math.ceil(filteredItems.length / this.settings().perPage.selected) || 1;
    this.resultCount.set(filteredItems.length);
    this.pagination.set({
      data: filteredItems,
      totalRecords: filteredItems.length,
      totalPages: totalPages,
      perPage: this.settings().perPage.selected,
      page: this.settings().page.selected
    });

    this.settings().page.values = Array.from({ length: totalPages }, (_, i) => i + 1);

    if (this.settings().page.selected > totalPages) {
      this.settings().page.selected = 1;
    }
  }

  select(item: QueryResultItem): void {
    this.picked.set(item);
    this.rowPicked.emit(item);
    this.valueChange.emit(item.id ?? null);
    this.close();
  }

  close(): void {
    this.showModal.set(false);
    this.searchTerm.set('');
  }
}
