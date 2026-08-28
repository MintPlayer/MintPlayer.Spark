import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, output, signal, viewChild, TemplateRef } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Subscription } from 'rxjs';
import { CommonModule, NgTemplateOutlet } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, ParamMap, Router } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsGridComponent, BsGridRowDirective, BsGridColumnDirective } from '@mintplayer/ng-bootstrap/grid';
import { BsInputGroupComponent } from '@mintplayer/ng-bootstrap/input-group';
import { BsPriorityNavComponent, BsPriorityNavItemDirective } from '@mintplayer/ng-bootstrap/priority-nav';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { HttpErrorResponse } from '@angular/common/http';
import { SparkService, SparkStreamingService, SparkLanguageService } from '@mintplayer/ng-spark/services';
import { TranslateKeyPipe, ResolveTranslationPipe } from '@mintplayer/ng-spark/pipes';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';
import { SparkQueryGridComponent } from '@mintplayer/ng-spark/grid';
import {
  CustomActionDefinition,
  StreamingMessage,
  QueryColumn,
  QueryResultItem,
} from '@mintplayer/ng-spark/models';

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
  imports: [BsBadgeComponent, CommonModule, NgTemplateOutlet, FormsModule, BsAlertComponent, BsFormComponent, BsFormControlDirective, BsGridComponent, BsGridRowDirective, BsGridColumnDirective, BsInputGroupComponent, BsPriorityNavComponent, BsPriorityNavItemDirective, BsSpinnerComponent, SparkIconComponent, SparkQueryGridComponent, ResolveTranslationPipe, TranslateKeyPipe],
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
  protected readonly canCreate = computed(() => this.grid()?.canCreate() ?? false);
  protected readonly resultCount = computed(() => this.grid()?.resultCount() ?? null);
  protected readonly isVirtualScrolling = computed(() => this.grid()?.isVirtualScrolling() ?? false);
  protected readonly gridError = computed(() => this.grid()?.errorMessage() ?? null);

  /** Whether an action's selection rule is satisfied. Delegated: the grid holds the selection. */
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
      items = [...items].sort((a, b) => {
        for (const col of sortCols) {
          const aVal = a.values.find(v => v.key === col.property)?.value ?? '';
          const bVal = b.values.find(v => v.key === col.property)?.value ?? '';
          const cmp = String(aVal).localeCompare(String(bVal));
          if (cmp !== 0) return col.direction === 'descending' ? -cmp : cmp;
        }
        return 0;
      });
    }

    this.streamItems.set(items);
  }
}
