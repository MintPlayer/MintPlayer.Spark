import { ChangeDetectionStrategy, Component, computed, effect, inject, input, model, output, signal, untracked, Type } from '@angular/core';
import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { RouterModule } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsDatatableComponent, BsDatatableColumnDirective, BsRowTemplateDirective, DatatableSettings, type BsDatatableFetch } from '@mintplayer/ng-bootstrap/datatable';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { SparkQueryRefreshService } from '@mintplayer/ng-spark/client-operations';
import { cellValue } from '@mintplayer/ng-spark/renderers';
import { QueryCellValuePipe, QueryReferenceChipsPipe, ResolveTranslationPipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkAttributeDescriptionComponent } from '@mintplayer/ng-spark/attribute-description';
import { SparkLanguageService, SparkService } from '@mintplayer/ng-spark/services';
import {
  CustomActionDefinition,
  EntityAttributeDefinition,
  EntityType,
  LookupReference,
  QueryColumn,
  QueryResultItem,
  SparkQuery,
  filterQueryActions,
  parseSelectionRule,
  selectionModeFor,
  valueFor,
} from '@mintplayer/ng-spark/models';
import { SPARK_GRID_PAGE_SIZES, initialGridSettings, isVirtualScrollingQuery } from './spark-grid-columns';
import { SparkGridRenderers } from './spark-grid-renderers';
import { SparkGridCellComponent } from './spark-grid-cell.component';

/**
 * The one Spark grid: a `<bs-datatable>` rendering a query or a sub-query.
 *
 * This replaces two components that were the same grid written twice — and, counting
 * `spark-query-list`'s streaming and paged branches, the `<bs-datatable>` element itself written
 * three times. The duplication had already produced four user-visible bugs, each fixed on one
 * side and not the other: `[indeterminate]` on null booleans, permission state surviving a failed
 * reload, a swallowed fetch failure, and virtual-scroll sizing. `SparkGridRenderers` was
 * extracted to stop that and could only take the stateless helpers with it; this takes the rest.
 *
 * ## What it deliberately does NOT own: streaming
 *
 * `spark-grid-renderers.ts` records why — a websocket dependency graph must not reach the bundle
 * of every PO detail page, none of which stream. So rows can come from **either** side:
 *
 *  - unbound `data` — the grid fetches for itself, paging and sorting server-side;
 *  - bound `data` — the grid renders what it is given and never fetches.
 *
 * `spark-query-list` keeps the socket and feeds its snapshot in. This is the line `bs-datatable`
 * itself draws: `[data]` and `[fetch]` are mutually exclusive, and binding both makes `[fetch]`
 * win silently.
 *
 * ## Reading its state
 *
 * `query`, `entityType`, `customActions`, `selection`, `canRead`, `canCreate` and `resultCount`
 * are readable so a host can render chrome around the grid. Read them through a template
 * reference variable — `<spark-query-grid #grid>` … `grid.query()` — not `viewChild`: hosts wrap
 * grids in `@if`, where a `viewChild` is intermittently undefined.
 */
@Component({
  selector: 'spark-query-grid',
  imports: [CommonModule, RouterModule, BsAlertComponent, BsDatatableComponent, BsDatatableColumnDirective, BsRowTemplateDirective, BsSpinnerComponent, SparkGridCellComponent, ResolveTranslationPipe, QueryCellValuePipe, QueryReferenceChipsPipe, TranslateKeyPipe, SparkAttributeDescriptionComponent],
  templateUrl: './spark-query-grid.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkQueryGridComponent {
  private readonly sparkService = inject(SparkService);
  private readonly gridRenderers = inject(SparkGridRenderers);
  private readonly queryRefresh = inject(SparkQueryRefreshService);
  readonly lang = inject(SparkLanguageService);

  /** Query alias or id. */
  queryId = input.required<string>();

  /**
   * The parent persistent object this query is scoped to, when it has one.
   *
   * Optional, because not every query is a detail of something: a page can host a grid that
   * stands on its own — "my accounts", a dashboard list — and the server already treats an absent
   * parent as "no parent" rather than as an error. Requiring these made that shape impossible to
   * express: the component simply never loaded, with no request, no error and no log.
   *
   * Pass both or neither. One without the other is ignored, matching `SparkService.executeQuery`,
   * which omits either param when it is falsy, and the execute endpoint, which resolves a parent
   * only when both are present.
   */
  parentId = input<string>('');
  parentType = input<string>('');

  /**
   * Rows supplied from outside. When bound, the grid renders these and runs no fetch.
   *
   * This is how streaming stays out of this component (see the class comment). `null` — the
   * default — means "fetch for yourself"; an empty array means "you have been given no rows",
   * which is a different statement and renders an empty grid rather than triggering a load.
   */
  data = input<QueryResultItem[] | null>(null);

  /**
   * Server-side search term, passed through to the query.
   *
   * An input rather than a box, so the host owns the control and the grid owns the request. A
   * host with client-side rows filters them itself before binding `data`.
   */
  search = input<string>('');

  /**
   * Column metadata for externally supplied rows. Required whenever `data` is bound, because a
   * projection cannot describe itself — there is no per-row metadata left to infer it from.
   */
  columns = input<QueryColumn[] | null>(null);

  /**
   * Change this to re-run the query. Any value works; only its identity matters.
   *
   * A declarative token rather than only a `reload()` method, because calling a method means
   * holding a component handle, and hosts wrap this grid in `@if`, where a `viewChild` is
   * intermittently undefined.
   *
   * This drives the CHEAP refresh (see {@link reload}). It deliberately does not feed the main
   * effect: re-running `loadData` would re-resolve the query, the entity types, the permissions
   * and the lookups, and reset the user's page and sort on every button press.
   */
  reloadToken = input<unknown>(null);

  /**
   * Paging and sorting. Two-way, so a host driving `data` can sort those rows by whatever the
   * user actually clicked — the datatable writes the new sort back through here.
   */
  settings = model(new DatatableSettings({
    perPage: { values: SPARK_GRID_PAGE_SIZES, selected: SPARK_GRID_PAGE_SIZES[0] },
    page: { values: [1], selected: 1 },
    sortColumns: [],
  }));

  /** Rows the user has ticked. Two-way so chrome outside the grid can read and clear them. */
  selection = model<QueryResultItem[]>([]);

  /**
   * Replaces where the first column's link points, per row.
   *
   * Return `null` to suppress the link for that row — useful when a grid mixes rows that have a
   * detail page with rows that do not.
   *
   * ⚠️ **It changes the destination, not the permission.** `canRead()` still gates whether any
   * link is rendered at all, and this function is never consulted when that gate is closed. It
   * cannot be used to reach a row the rights model withheld; it exists for the case where the
   * row's natural destination is not `/po/{type}/{id}` — a composed row that maps to a page of
   * its own, or a grid embedded in a route that owns its own child paths.
   */
  rowRoute = input<((row: QueryResultItem) => unknown[] | null) | null>(null);

  /** Emitted whenever a load or a page fetch fails, for a host in bespoke chrome. */
  error = output<HttpErrorResponse>();
  rowClicked = output<QueryResultItem>();
  customActionExecuted = output<{ action: CustomActionDefinition }>();

  colors = Color;

  /**
   * Where the first column's link points for this row: the host's `rowRoute` when it supplied one,
   * otherwise the row's own detail page.
   *
   * A method rather than a computed because the answer is per row, and the template already
   * iterates rows — a computed would have to be a map keyed by id, rebuilt on every page.
   */
  protected rowRouteFor(row: QueryResultItem): unknown[] | null {
    const custom = this.rowRoute();
    if (custom) return custom(row);

    const type = this.entityType();

    // ⚠️ A composed type has no per-row detail page by construction: its rows are computed, and
    // `{Name}Actions.OnLoadAsync` serves ONE page which is free to ignore the id it was given. So
    // /po/{type}/{rowId} does not 404 — it silently renders that same page, which is worse, because
    // nothing looks wrong. This branch is live in DemoApp today: Read/StartPage implies
    // Query/StartPage, so the composed grid renders with canRead() true and every row linked.
    //
    // `clrType` is absent exactly for those types and is already on the wire; the grid simply never
    // consulted it. A host whose composed rows DO have a page says so with `rowRoute`.
    if (!type?.clrType) return null;

    return ['/po', type.alias || type.id, row.id];
  }

  query = signal<SparkQuery | null>(null);
  entityType = signal<EntityType | null>(null);
  allEntityTypes = signal<EntityType[]>([]);
  lookupReferenceOptions = signal<Record<string, LookupReference>>({});
  loading = signal(true);
  canRead = signal(false);
  canCreate = signal(false);
  resultCount = signal<number | null>(null);
  customActions = signal<CustomActionDefinition[]>([]);
  fetchFn = signal<BsDatatableFetch<QueryResultItem> | null>(null);

  /**
   * Why the component renders its own failure instead of only reporting one.
   *
   * `SparkService` is a bare `firstValueFrom` passthrough with no interceptor, so every failure
   * surfaces here and nowhere else. A host embedding this grid cannot surface what it never sees,
   * and the default has to be visible with no host cooperation — hence a rendered alert, not just
   * an output.
   */
  errorMessage = signal<string | null>(null);

  /** 'none' unless an action is selection-gated, so unaffected grids gain no checkbox column. */
  selectionMode = computed(() => selectionModeFor(this.customActions()));

  isVirtualScrolling = computed(() => isVirtualScrollingQuery(this.query()));

  /**
   * Columns as sent with the most recent fetched page.
   *
   * A host binding `data` (streaming) supplies its own via the `columns` input instead — the
   * snapshot carries them once, exactly as a paged result does.
   */
  private readonly fetchedColumns = signal<QueryColumn[]>([]);

  /** Every column on the wire, drawn or not — what renderers and lookups resolve against. */
  allColumns = computed(() => this.columns() ?? this.fetchedColumns());

  /**
   * The columns the grid draws. `isVisible: false` ships a value without a column, so a renderer
   * can read a sibling the grid does not show; undefined means visible, so a server that predates
   * the field still draws everything.
   */
  visibleColumns = computed(() => this.allColumns().filter(c => c.isVisible !== false));

  /**
   * True when rows do not come from this component's own fetch.
   *
   * Either because the host bound `data`, or because the query streams — a streaming query's rows
   * arrive over a socket and `/execute` is not a way to get them. The grid recognises the second
   * case itself rather than waiting to be told, because it resolves the query before the host can
   * see it: left to `data` alone, every streaming grid would fire one pointless fetch on mount,
   * before the host had anything to bind.
   */
  hasExternalData = computed(() =>
    this.data() !== null || this.query()?.isStreamingQuery === true);

  /** Whether an action's selection rule is satisfied right now. The server checks it again. */
  isActionEnabled(action: CustomActionDefinition): boolean {
    return parseSelectionRule(action.selectionRule)(this.selection().map(r => r.id).length);
  }

  constructor() {
    effect(() => {
      const qId = this.queryId();
      const pId = this.parentId();
      const pType = this.parentType();
      // Only the query id is required. Requiring a parent here is what made a standalone grid
      // silently render nothing.
      if (qId) {
        this.loadData(qId, pId, pType);
      } else {
        // No query id at all. `loading` starts true, so without this the component would spin
        // forever instead of saying anything.
        this.loading.set(false);
      }
    });

    // Separate effect, so the token drives the cheap refresh and never the full metadata reload.
    // `first` skips the initial run: the effect above has already fetched, and reacting to the
    // token's starting value would double-fetch on mount.
    let first = true;
    effect(() => {
      // Both the host's token and the server's refreshQuery drive the same cheap refresh.
      this.reloadToken();
      this.queryRefresh.tokenFor(this.queryId());
      if (first) { first = false; return; }
      untracked(() => this.reload());
    });

    // A new search term must refetch even when page, perPage and sort are unchanged — the
    // datatable dedupes reloads by exactly that triple, and a new fetch identity is what resets
    // its dedupe key. Skipping the first run keeps mount to a single request.
    let firstSearch = true;
    effect(() => {
      this.search();
      if (firstSearch) { firstSearch = false; return; }
      untracked(() => this.onSearchChanged());
    });
  }

  /**
   * Re-run the query, keeping the current page, sort and scroll position.
   *
   * Data-level on purpose: it re-seeds the fetch closure and nothing else. For a definition
   * change — new columns, a renamed query — the inputs themselves must change; that is the
   * expensive path. Inert when rows come from the host: there is nothing here to re-run.
   */
  reload(): void {
    if (this.hasExternalData()) return;
    const q = this.query();
    if (q) this.fetchFn.set(this.makeFetch(q, this.parentId(), this.parentType()));
  }

  private onSearchChanged(): void {
    if (this.hasExternalData()) return;
    const s = this.settings();
    // Back to page 1: the old page number means nothing against a different result set.
    this.settings.set(new DatatableSettings({
      perPage: { values: s.perPage.values, selected: s.perPage.selected },
      page: { values: [1], selected: 1 },
      sortColumns: s.sortColumns,
    }));
    this.reload();
  }

  async onCustomAction(action: CustomActionDefinition): Promise<void> {
    if (action.confirmationMessageKey) {
      const message = this.lang.t(action.confirmationMessageKey) || 'Are you sure?';
      if (!confirm(message)) return;
    }
    try {
      // The sub-query's container travels with the action (#327). Without it an action invoked
      // from a company's Cars tab could not tell it was on a company's page at all — the grid knew
      // (it filters the rows by that parent) and simply dropped the fact on the way out.
      //
      // Sent as parentId/parentType, NOT as the `parent` argument: that one means an object of this
      // action's OWN type and is loaded under it server-side, so handing it a Company id for a Car
      // action refuses — correctly, and confusingly.
      const pId = this.parentId();
      const pType = this.parentType();
      await this.sparkService.executeCustomAction(
        this.entityType()!.id,
        action.name,
        undefined,
        this.selection().map(r => r.id),
        pId && pType ? { id: pId, type: pType } : undefined,
        // The query too: the server re-runs it narrowed to these ids, so the action receives the
        // rows this grid rendered rather than a re-derivation from documents.
        this.query()?.id);
      this.customActionExecuted.emit({ action });
      if (action.refreshOnCompleted) this.reload();
    } catch (e) {
      const err = e as HttpErrorResponse;
      this.errorMessage.set(err.error?.error || err.message || this.lang.t('common.actionFailed') || 'Action failed');
    }
  }

  private reportError(err: HttpErrorResponse): void {
    this.errorMessage.set(this.describe(err));
    this.error.emit(err);
  }

  /**
   * A 404 is deliberately generic.
   *
   * `Endpoints/Queries/Get.cs` answers 404 with byte-identical bodies for "no such query" and
   * "you may not see it", so existence is not disclosed (security audit M-3). The component
   * therefore genuinely cannot tell them apart, and both "Not found" and "Access denied" would be
   * a guess — one of them leaking, the other misleading.
   */
  private describe(err: HttpErrorResponse): string {
    if (err?.status === 404) {
      return this.lang.t('spark.query.unavailable') || 'This list is not available.';
    }
    return err?.error?.error || err?.message
      || this.lang.t('common.unexpectedError') || 'An unexpected error occurred';
  }

  private async loadData(queryId: string, parentId: string, parentType: string): Promise<void> {
    this.loading.set(true);
    this.errorMessage.set(null);
    this.fetchFn.set(null);
    // Reset everything derived from the previous query, not just the fetch. Leaving
    // `entityType`/`canRead` behind let a failed reload build a row link out of the PREVIOUS type
    // and the previous permission.
    this.query.set(null);
    this.entityType.set(null);
    this.canRead.set(false);
    this.canCreate.set(false);
    this.customActions.set([]);
    this.resultCount.set(null);
    // Ids from the previous query are meaningless against the next one, and would be POSTed as
    // though they belonged to it.
    this.selection.set([]);
    try {
      const [resolvedQuery, entityTypes] = await Promise.all([
        this.sparkService.getQuery(queryId),
        this.sparkService.getEntityTypes(),
      ]);

      this.query.set(resolvedQuery);
      this.allEntityTypes.set(entityTypes);

      const et = this.resolveEntityType(resolvedQuery, entityTypes);
      this.entityType.set(et);
      if (et) {
        const [permissions, actions] = await Promise.all([
          this.sparkService.getPermissions(et.id),
          this.sparkService.getCustomActions(et.id),
        ]);
        this.canRead.set(permissions.canRead);
        this.canCreate.set(permissions.canCreate);
        // 'query', not 'list'. The server model has always documented "detail" | "query" |
        // "both"; a filter testing for a value nothing emits renders the action NOWHERE.
        this.customActions.set(filterQueryActions(actions));
      }

      this.settings.set(initialGridSettings(resolvedQuery));
      // The datatable drives paging/sorting via [(settings)] and calls fetchFn per page. Virtual
      // scrolling is just the [virtualScroll] template flag.
      // `resolvedQuery`, not `hasExternalData()`: the query signal is set above, but reading the
      // computed here would depend on signal-read ordering inside an async method. Ask the value
      // directly.
      if (this.data() === null && !resolvedQuery?.isStreamingQuery) {
        this.fetchFn.set(this.makeFetch(resolvedQuery, parentId, parentType));
      }

      this.loadLookupReferenceOptions();
    } catch (e) {
      this.fetchFn.set(null);
      this.reportError(e as HttpErrorResponse);
    } finally {
      this.loading.set(false);
    }
  }

  /**
   * The query's entity type.
   *
   * The source-name fallback is not optional garnish: a `Database.*` query need not declare
   * `entityType`, and the sub-query grid — which matched on the declared name alone — rendered
   * those as an empty card with no columns, no rows and no error, while the identical query
   * rendered correctly as a page. Both grids are this one now, so there is one answer.
   */
  private resolveEntityType(query: SparkQuery | null, entityTypes: EntityType[]): EntityType | null {
    if (!query) return null;

    if (query.entityType) {
      return entityTypes.find(t =>
        t.name === query.entityType || t.alias === query.entityType?.toLowerCase()) ?? null;
    }

    const sourceName = extractSourceName(query.source);
    const singular = singularize(sourceName);
    return entityTypes.find(t =>
      t.name === sourceName || t.name === singular || t.clrType?.endsWith(singular)) ?? null;
  }

  private makeFetch(query: SparkQuery, parentId: string, parentType: string): BsDatatableFetch<QueryResultItem> {
    return (req) => this.sparkService.executeQuery(query.id, {
      sortColumns: req.sortColumns,
      skip: (req.page - 1) * req.perPage,
      take: req.perPage,
      search: this.search() || undefined,
      parentId, parentType,
    }).then(r => {
      this.errorMessage.set(null);
      this.resultCount.set(r.totalItems);
      // Columns come from the result now, not from the entity type: the server decides the query
      // surface (ShowedOn.Query) and is the same place the sort-column allow-list is checked.
      // ?? [] because a malformed or older response must render an empty grid, not throw inside a
      // computed — where the stack points at the column filter and not at the response that lacked them.
      this.fetchedColumns.set(r.columns ?? []);
      return {
        data: r.items,
        totalRecords: r.totalItems,
        totalPages: Math.ceil(r.totalItems / req.perPage) || 1,
        perPage: req.perPage,
        page: req.page,
      };
    }).catch((e: HttpErrorResponse) => {
      // Report before returning the empty page, or a failed fetch is indistinguishable from a
      // query that legitimately has no rows.
      this.reportError(e);
      this.resultCount.set(0);
      return { data: [], totalRecords: 0, totalPages: 1, perPage: req.perPage, page: req.page };
    });
  }

  private async loadLookupReferenceOptions(): Promise<void> {
    // Over ALL columns, not just the drawn ones: an undrawn column's value can still be rendered
    // by a renderer reading it as a sibling, and it would resolve to a raw key without its options.
    this.lookupReferenceOptions.set(await this.gridRenderers.loadLookupOptions(this.allColumns()));
  }

  /**
   * The underlying value a custom renderer receives, which is not the printable one.
   *
   * A projection is flat, so this is the cell's own value rather than a display string. It is
   * deliberately not the attribute-shaped `rendererValue`: a query row has no nested object to
   * fall back to, and reaching for one would hand every renderer `undefined`.
   */
  rendererValueFor(item: QueryResultItem, column: QueryColumn): unknown {
    return cellValue(valueFor(item, column.name));
  }

  getColumnRendererComponent(column: QueryColumn): Type<any> | null {
    return this.gridRenderers.columnComponentFor(column);
  }

  getColumnRendererInputs(component: Type<any>, item: QueryResultItem, column: QueryColumn): Record<string, any> {
    return this.gridRenderers.columnInputsFor(component, item, column);
  }
}

function extractSourceName(source: string): string {
  const dotIndex = source.indexOf('.');
  return dotIndex >= 0 ? source.substring(dotIndex + 1) : source;
}

/**
 * Enough English to map a query source onto a type name — `Cars` to `Car`, `People` to `Person`.
 *
 * Deliberately a small table plus three suffix rules rather than a library: it runs against type
 * names the application itself chose, and a wrong guess costs nothing, since the caller falls
 * through to matching the plural and the CLR type name.
 */
function singularize(plural: string): string {
  const irregulars: Record<string, string> = {
    'People': 'Person',
    'Children': 'Child',
    'Men': 'Man',
    'Women': 'Woman',
  };
  if (irregulars[plural]) return irregulars[plural];
  if (plural.endsWith('ies')) return plural.slice(0, -3) + 'y';
  if (plural.endsWith('es')) return plural.slice(0, -2);
  if (plural.endsWith('s')) return plural.slice(0, -1);
  return plural;
}
