import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, output, signal, viewChild, TemplateRef } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Subscription } from 'rxjs';
import { CommonModule, NgTemplateOutlet } from '@angular/common';
import { ActivatedRoute, ParamMap, Router } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsGridComponent, BsGridRowDirective, BsGridColumnDirective } from '@mintplayer/ng-bootstrap/grid';
import { BsPriorityNavComponent, BsPriorityNavItemDirective } from '@mintplayer/ng-bootstrap/priority-nav';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { HttpErrorResponse } from '@angular/common/http';
import { SparkService, SparkStreamingService, SparkLanguageService } from '@mintplayer/ng-spark/services';
import { TranslateKeyPipe, ResolveTranslationPipe } from '@mintplayer/ng-spark/pipes';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';
import { SparkPriorityNavCloseOnActionDirective, SparkQueryGridComponent, SparkQueryToolbarAction, SparkSearchBoxComponent, sparkActionClass } from '@mintplayer/ng-spark/grid';
import {
  CustomActionDefinition,
  StreamingMessage,
  QueryColumn,
  QueryColumnFilter,
  QueryResultItem,
  SparkDeletedFilter,
  isDateDataType,
  parseWireDate,
  type SparkSelectionModeSetting,
} from '@mintplayer/ng-spark/models';
import { NgComponentOutlet } from '@angular/common';
import {
  SPARK_QUERY_LIST_ACTIONS,
  SparkQueryListContext,
  orderSparkExtensions,
  parseSparkDeletedParam,
} from '@mintplayer/ng-spark/panels';

/**
 * The routed query page: chrome around one {@link SparkQueryGridComponent}.
 *
 * It owns what is genuinely page-shaped and route-shaped, and nothing else:
 *
 *  - **Route resolution.** It has no `queryId` input; it reads `paramMap`, and it serves two
 *    routes — `query/:queryId`, and `po/:type`, which resolves an entity type to a query. That
 *    second one is type-to-query resolution, not query rendering, and is why this component still
 *    exists rather than the router pointing at the grid.
 *  - **Streaming.** The websocket lives here so it stays out of every PO detail page's bundle;
 *    the snapshot is filtered and sorted client-side and handed to the grid as `[data]`.
 *  - The action bar, the caption, the LIVE badge, the search box and the New button.
 *
 * The grid itself — columns, cells, paging, the row link, selection, custom-action execution —
 * is the shared component. This page previously wrote out `<bs-datatable>` twice, once per
 * transport, with a shared row template between them; both are gone.
 */
@Component({
  selector: 'spark-query-list',
  imports: [BsBadgeComponent, CommonModule, NgTemplateOutlet, NgComponentOutlet, BsAlertComponent, BsGridComponent, BsGridRowDirective, BsGridColumnDirective, BsPriorityNavComponent, BsPriorityNavItemDirective, SparkPriorityNavCloseOnActionDirective, BsSpinnerComponent, SparkIconComponent, SparkQueryGridComponent, SparkSearchBoxComponent, ResolveTranslationPipe, TranslateKeyPipe],
  templateUrl: './spark-query-list.component.html',
  styleUrl: './spark-query-list.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '[class.virtual-scrolling]': 'isVirtualScrolling()'
  }
})
export class SparkQueryListComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly sparkService = inject(SparkService);
  private readonly streamingService = inject(SparkStreamingService);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly lang = inject(SparkLanguageService);

  extraActionsTemplate = input<TemplateRef<void> | null>(null);
  showCustomActions = input(true);

  /**
   * Overrides the query's own `selectionMode` (#467, R3), as the sub-query card's input does: for a
   * host that embeds this page and wants, say, `'none'` on a list the actions would make selectable.
   */
  selectionMode = input<SparkSelectionModeSetting | null>(null);

  /**
   * Forwarded to the grid, so a query PAGE can replace its row links — the same reason the card
   * forwards it. Without this the escape hatch existed only for a directly-embedded grid.
   */
  rowRoute = input<((row: QueryResultItem) => unknown[] | null) | null>(null);

  rowClicked = output<QueryResultItem>();
  createClicked = output<void>();
  customActionExecuted = output<{ action: CustomActionDefinition }>();

  colors = Color;

  /** The query the grid should render, resolved from the route. Null until it is known. */
  queryId = signal<string | null>(null);
  errorMessage = signal<string | null>(null);
  searchTerm = signal('');

  private readonly grid = viewChild(SparkQueryGridComponent);

  /**
   * Grid state, surfaced for this page's chrome.
   *
   * Optional `viewChild`, read defensively: the action bar and caption render above the grid, so
   * on the first change-detection pass the query has not resolved yet.
   */
  protected readonly query = computed(() => this.grid()?.query() ?? null);
  protected readonly entityType = computed(() => this.grid()?.entityType() ?? null);
  protected readonly customActions = computed(() => this.grid()?.customActions() ?? []);
  protected readonly canCreate = computed(() => this.grid()?.offersCreate() ?? false);
  protected readonly resultCount = computed(() => this.grid()?.resultCount() ?? null);
  protected readonly isVirtualScrolling = computed(() => this.grid()?.isVirtualScrolling() ?? false);
  protected readonly gridError = computed(() => this.grid()?.errorMessage() ?? null);
  protected readonly permissions = computed(() => this.grid()?.permissions() ?? null);

  /** Add-on buttons for the action bar (#460, `SPARK_QUERY_LIST_ACTIONS`), e.g. the Deleted toggle. */
  protected readonly listActions = orderSparkExtensions(inject(SPARK_QUERY_LIST_ACTIONS, { optional: true }), a => a.priority ?? 60);

  /**
   * The soft-deletion mode (#460, T2), read from the route's `?deleted=` so the recycle bin survives
   * a reload and the back button from a row opened in it. `exclude` unless the route says otherwise.
   */
  protected readonly deletedMode = signal<SparkDeletedFilter>('exclude');

  /** Bumped by `context.reload()`; the grid treats any new value as "re-run the query". */
  private readonly reloadToken = signal(0);

  protected readonly listContext = computed((): SparkQueryListContext | null => {
    const query = this.query();
    if (!query) return null;
    return {
      query,
      entityType: this.entityType(),
      permissions: this.permissions(),
      deleted: this.deletedMode(),
      setDeleted: (mode: SparkDeletedFilter) => this.setDeleted(mode),
      reload: () => this.reloadToken.update(n => n + 1),
    };
  });

  protected readonly gridReloadToken = this.reloadToken.asReadonly();

  private setDeleted(mode: SparkDeletedFilter): void {
    this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { deleted: mode === 'exclude' ? null : mode },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  /** Whether an action's selection rule is satisfied. Delegated: the grid holds the selection. */
  /** @see SparkPoDetailComponent.customActionClass — same allow-list, same default. */
  protected customActionClass(action: CustomActionDefinition): string {
    const variant = action.variant?.toLowerCase();
    switch (variant) {
      case 'danger':
      case 'warning':
      case 'primary':
      case 'secondary':
      case 'success':
        return `btn btn-${variant}`;
      default:
        return 'btn btn-outline-primary';
    }
  }

  /** The grid's toolbar model (#460, M15), the same list the sub-query card renders. */
  protected readonly toolbarActions = computed(() => this.grid()?.toolbarActions() ?? []);

  protected toolbarActionClass(action: SparkQueryToolbarAction): string {
    // New keeps its solid primary look; the rest follow their variant.
    return action.kind === 'new' ? 'btn btn-primary' : sparkActionClass(action.definition);
  }

  protected isToolbarActionEnabled(action: SparkQueryToolbarAction): boolean {
    return this.grid()?.isToolbarActionEnabled(action) ?? false;
  }

  /** The tooltip of an action disabled for want of its client method (`requiresClient`); null otherwise. */
  protected toolbarActionUnavailableReason(action: SparkQueryToolbarAction): string | null {
    return this.grid()?.actionUnavailableReason(action.definition) ?? null;
  }

  protected runToolbarAction(action: SparkQueryToolbarAction): void {
    if (action.kind === 'new') {
      // Through this page's own onCreate, so `createClicked` fires exactly as it always has.
      this.onCreate();
      return;
    }
    void this.grid()?.runToolbarAction(action);
  }

  protected isActionEnabled(action: CustomActionDefinition): boolean {
    return this.grid()?.isActionEnabled(action) ?? false;
  }

  // --- streaming ---------------------------------------------------------------

  isStreaming = signal(false);
  private streamingSub: Subscription | null = null;
  /** Columns as sent with the stream's snapshot; empty until it arrives. */
  protected readonly streamColumns = signal<QueryColumn[]>([]);
  private readonly allItems = signal<QueryResultItem[]>([]);
  private readonly streamItems = signal<QueryResultItem[]>([]);

  /**
   * Rows handed to the grid, or `null` to let it fetch for itself.
   *
   * Null for a normal query — an empty array would read as "here are no rows" and suppress the
   * fetch entirely.
   */
  protected readonly gridData = computed(() =>
    this.query()?.isStreamingQuery ? this.streamItems() : null);

  constructor() {
    this.route.paramMap.pipe(takeUntilDestroyed()).subscribe(params => {
      // The handler is async and this is a subscribe, so a rejection lands nowhere: the metadata
      // load would reject, the query would stay null, and the template would render a spinner
      // FOREVER — while this component has had an errorMessage surface all along that only the
      // fetch path ever reached.
      this.onParamsChange(params).catch((e: unknown) => this.reportLoadFailure(e as HttpErrorResponse));
    });

    // Separate from paramMap: switching the recycle bin on changes only the query string, and must
    // not re-resolve the query (which would reset page, sort and filters for nothing).
    this.route.queryParamMap.pipe(takeUntilDestroyed()).subscribe(query => {
      this.deletedMode.set(parseSparkDeletedParam(query.get('deleted')) ?? 'exclude');
      this.applyUrlScope(query);
    });

    this.destroyRef.onDestroy(() => this.disconnectStreaming());

    // Connect the socket once the grid has resolved a streaming query, and disconnect whenever it
    // resolves anything else. The grid knows not to fetch for a streaming query, so there is no
    // window in which both transports are live.
    effect(() => {
      const q = this.query();
      if (q?.isStreamingQuery) {
        this.connectStreaming(q.id);
      } else {
        this.disconnectStreaming();
      }
    });

    // Client-side filter and sort for the streaming snapshot. The server never sees these: there
    // is no request to attach them to.
    effect(() => {
      this.searchTerm();
      this.grid()?.settings();
      this.allItems();
      if (this.isStreaming()) this.applyFilter();
    });
  }

  // --- URL scope: parent and column filters ----------------------------------------------------

  /**
   * The parent the URL scopes the query to (`?parentId=…&parentType=…`), sent exactly as a sub-query
   * sends its container. Both or neither: one without the other is ignored, as the server does.
   */
  protected readonly urlParentId = signal('');
  protected readonly urlParentType = signal('');

  /**
   * Column filters from the URL: every other query-string parameter, `?Language=en&Script=Latn`
   * (repeat a name for several values). The grid applies only those naming an attribute of the
   * query's type; the rest are ignored. This is the contributions History link's shape, and works for
   * any query.
   */
  protected readonly urlFilters = signal<QueryColumnFilter[]>([]);

  /** The URL filters the grid applied, as removable chips. */
  protected readonly activeUrlFilters = computed(() => this.grid()?.appliedPresetFilters() ?? []);

  /** Query-string names the page or the row links own, never column filters. */
  private static readonly reservedParams = new Set(['deleted', 'parentId', 'parentType', 'queryId', 'parentDeleted']);

  private applyUrlScope(query: ParamMap): void {
    const parentId = query.get('parentId') ?? '';
    const parentType = query.get('parentType') ?? '';
    const both = !!parentId && !!parentType;
    if (this.urlParentId() !== (both ? parentId : '')) this.urlParentId.set(both ? parentId : '');
    if (this.urlParentType() !== (both ? parentType : '')) this.urlParentType.set(both ? parentType : '');

    const filters: QueryColumnFilter[] = query.keys
      .filter(key => !SparkQueryListComponent.reservedParams.has(key))
      .map(key => ({ name: key, includes: query.getAll(key).filter(v => v !== '') }))
      .filter(f => f.includes.length > 0);
    // Only on a real change: a new array would refetch the grid for nothing (the Deleted toggle
    // also changes the query string).
    if (JSON.stringify(filters) !== JSON.stringify(this.urlFilters())) this.urlFilters.set(filters);
  }

  /** Drops one URL filter (its query-string parameter); the grid refetches. */
  protected clearUrlFilter(name: string): void {
    this.router.navigate([], { relativeTo: this.route, queryParams: { [name]: null }, queryParamsHandling: 'merge', replaceUrl: true });
  }

  /** Drops the parent and every URL filter. */
  protected clearUrlScope(): void {
    const queryParams: Record<string, null> = { parentId: null, parentType: null };
    for (const filter of this.urlFilters()) queryParams[filter.name] = null;
    this.router.navigate([], { relativeTo: this.route, queryParams, queryParamsHandling: 'merge', replaceUrl: true });
  }

  protected filterValues(filter: QueryColumnFilter): string {
    return (filter.includes ?? []).map(v => String(v)).join(', ');
  }

  /** The column's label for a chip: the query's own column label, else the attribute's, else its name. */
  protected filterLabel(name: string): string {
    const attribute = this.entityType()?.attributes?.find(a => a.name === name);
    return this.lang.resolve(attribute?.label) || name;
  }

  private async onParamsChange(params: ParamMap): Promise<void> {
    this.errorMessage.set(null);
    this.queryId.set(null);
    this.allItems.set([]);
    this.streamItems.set([]);
    this.streamColumns.set([]);
    this.disconnectStreaming();

    const queryId = params.get('queryId');
    const typeParam = params.get('type');

    if (queryId) {
      this.queryId.set(queryId);
      return;
    }

    if (!typeParam) return;

    // `po/:type` — the route names an entity type, so find the query that lists it. The grid takes
    // a query, and this translation is the only reason it cannot take the route directly.
    const [entityTypes, queries] = await Promise.all([
      this.sparkService.getEntityTypes(),
      this.sparkService.getQueries(),
    ]);
    const entityType = entityTypes.find(t => t.id === typeParam || t.alias === typeParam);
    if (!entityType) {
      this.reportLoadFailure({ status: 404 } as HttpErrorResponse);
      return;
    }

    const singularName = entityType.name;
    const match = queries.find(q => {
      if (q.entityType === singularName) return true;
      const sourceName = q.source.includes('.') ? q.source.substring(q.source.indexOf('.') + 1) : q.source;
      return sourceName === singularName || sourceName === singularName + 's';
    });

    if (match) this.queryId.set(match.alias || match.id);
    else this.reportLoadFailure({ status: 404 } as HttpErrorResponse);
  }

  /**
   * A load failure has to render, not just be swallowed: a denied query answers 404 (audit M-3, so
   * existence is not leaked), which is indistinguishable from a missing one — hence a deliberately
   * generic message rather than a guess at which it was.
   */
  private reportLoadFailure(err: HttpErrorResponse): void {
    this.errorMessage.set(
      err?.status === 404
        ? (this.lang.t('spark.query.unavailable') || 'This list is not available.')
        : (err?.error?.error || err?.message || 'An unexpected error occurred'));
  }

  protected async onCustomAction(action: CustomActionDefinition): Promise<void> {
    await this.grid()?.onCustomAction(action);
  }

  protected onCreate(): void {
    this.createClicked.emit();
    const et = this.entityType();
    if (et) this.router.navigate(['/po', et.alias || et.id, 'new']);
  }

  protected clearSearch(): void {
    this.searchTerm.set('');
  }

  private connectStreaming(queryId: string): void {
    if (this.streamingSub) return;
    this.isStreaming.set(true);

    this.streamingSub = this.streamingService.connectToStreamingQuery(queryId).subscribe({
      next: (message) => this.handleStreamingMessage(message),
      error: (err) => {
        this.errorMessage.set(err?.message || 'Streaming connection failed');
        this.isStreaming.set(false);
      },
      complete: () => this.isStreaming.set(false),
    });
  }

  private disconnectStreaming(): void {
    if (this.streamingSub) {
      this.streamingSub.unsubscribe();
      this.streamingSub = null;
    }
    this.isStreaming.set(false);
  }

  private handleStreamingMessage(message: StreamingMessage): void {
    switch (message.type) {
      case 'snapshot':
        this.errorMessage.set(null);
        // Columns come with the snapshot and never change for the life of the stream, so they are
        // stored once and handed to the grid alongside the rows — a projection cannot describe
        // itself, and the grid has no entity-type metadata to fall back on any more.
        this.streamColumns.set(message.columns);
        this.allItems.set(message.data);
        break;

      case 'patch':
        if (message.updated.length > 0) {
          this.allItems.update(items => items.map(item => {
            const patch = message.updated.find(u => u.id === item.id);
            if (!patch) return item;
            return {
              ...item,
              values: item.values.map(v =>
                v.key in patch.values ? { ...v, value: patch.values[v.key] } : v),
            };
          }));
        }
        break;

      case 'error':
        this.errorMessage.set(message.message);
        break;
    }
  }

  private applyFilter(): void {
    let items = this.allItems();

    const term = this.searchTerm().toLowerCase();
    if (term) {
      items = items.filter(item =>
        item.values.some(v => String(v.value ?? '').toLowerCase().includes(term)));
    }

    // The datatable in `[data]` mode also sorts on header clicks, but the sort must survive a
    // patch: re-deriving from `allItems` without re-applying it would silently reorder the grid
    // under the user on every update.
    const sortCols = this.grid()?.settings().sortColumns ?? [];
    if (sortCols.length > 0) {
      const dataTypes = new Map(this.streamColumns().map(c => [c.name, c.dataType]));
      items = [...items].sort((a, b) => {
        for (const col of sortCols) {
          const aVal = a.values.find(v => v.key === col.property)?.value;
          const bVal = b.values.find(v => v.key === col.property)?.value;
          const cmp = compareStreamValues(aVal, bVal, dataTypes.get(col.property));
          if (cmp !== 0) return col.direction === 'descending' ? -cmp : cmp;
        }
        return 0;
      });
    }

    this.streamItems.set(items);
  }
}

/**
 * Orders two streamed cell values the way the server orders the same column.
 *
 * The values are what the wire carries: a date is the raw ISO string WITH its own offset and with
 * trailing fraction zeros trimmed (`…T09:00:00-05:00`, `…T09:00:00.5-05:00`), a number is a number.
 * Comparing them as text sorted dates by wall clock and `10` before `9`; a date compares by instant
 * and a number numerically (docs/datetimeoffset_query_sort_filter_PRD.md, F4).
 *
 * A missing value sorts first ascending, as the empty string always did.
 */
function compareStreamValues(a: unknown, b: unknown, dataType: string | undefined): number {
  const aMissing = a === null || a === undefined || a === '';
  const bMissing = b === null || b === undefined || b === '';
  if (aMissing || bMissing) return aMissing === bMissing ? 0 : aMissing ? -1 : 1;

  if (isDateDataType(dataType)) {
    const aDate = parseWireDate(a);
    const bDate = parseWireDate(b);
    if (aDate && bDate) return aDate.getTime() - bDate.getTime();
  } else if (dataType === 'number' || dataType === 'decimal') {
    const aNumber = Number(a);
    const bNumber = Number(b);
    if (!isNaN(aNumber) && !isNaN(bNumber)) return aNumber - bNumber;
  }

  return String(a).localeCompare(String(b));
}
