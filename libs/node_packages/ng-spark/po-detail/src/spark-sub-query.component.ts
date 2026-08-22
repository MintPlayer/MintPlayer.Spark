import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal, untracked, TemplateRef, Type } from '@angular/core';
import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { RouterModule } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsDatatableComponent, BsDatatableColumnDirective, BsRowTemplateDirective, DatatableSettings, type BsDatatableFetch } from '@mintplayer/ng-bootstrap/datatable';
import { BsPriorityNavComponent, BsPriorityNavItemDirective } from '@mintplayer/ng-bootstrap/priority-nav';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SortColumn } from '@mintplayer/pagination';
import { SparkService } from '@mintplayer/ng-spark/services';
import { SparkQueryRefreshService } from '@mintplayer/ng-spark/client-operations';
import { ResolveTranslationPipe, AttributeValuePipe, ReferenceChipsPipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { NgComponentOutlet } from '@angular/common';
import { SparkGridRenderers, initialGridSettings, isVirtualScrollingQuery, visibleGridAttributes, SPARK_GRID_PAGE_SIZES } from '@mintplayer/ng-spark/grid';
import {
  CustomActionDefinition,
  filterQueryActions,
  parseSelectionRule,
  selectionModeFor,
  EntityType,
  EntityAttributeDefinition,
  LookupReference,
  PersistentObject,
  SparkQuery,
  ShowedOn,
  hasShowedOnFlag,
} from '@mintplayer/ng-spark/models';

@Component({
  selector: 'spark-sub-query',
  imports: [CommonModule, NgComponentOutlet, RouterModule, BsAlertComponent, BsCardComponent, BsCardHeaderComponent, BsDatatableComponent, BsDatatableColumnDirective, BsRowTemplateDirective, BsPriorityNavComponent, BsPriorityNavItemDirective, BsSpinnerComponent, ResolveTranslationPipe, AttributeValuePipe, ReferenceChipsPipe, TranslateKeyPipe],
  templateUrl: './spark-sub-query.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class SparkSubQueryComponent {
  private readonly sparkService = inject(SparkService);
  private readonly gridRenderers = inject(SparkGridRenderers);
  readonly lang = inject(SparkLanguageService);

  queryId = input.required<string>();

  /**
   * The parent persistent object this query is scoped to, when it has one.
   *
   * Optional, because not every query is a detail of something: a page can host
   * a grid that stands on its own — "my accounts", a dashboard list — and the
   * server already treats an absent parent as "no parent" rather than as an
   * error. Leaving these required made that shape impossible to express: the
   * component simply never loaded, with no request, no error and no log.
   *
   * Pass both or neither. One without the other is ignored, matching
   * `SparkService.executeQuery`, which omits either param when it is falsy, and
   * the execute endpoint, which resolves a parent only when both are present.
   */
  parentId = input<string>('');
  parentType = input<string>('');

  /**
   * Change this to re-run the query. Any value works; only its identity matters.
   *
   * A declarative token rather than only a `reload()` method, because calling a
   * method means holding a component handle, and hosts wrap this grid in `@if`,
   * where a `viewChild` is intermittently undefined. Nothing else in ng-spark
   * uses `viewChild` either — the house idiom is to re-seed a signal.
   *
   * This drives the CHEAP refresh (see {@link reload}). It deliberately does not
   * feed the main effect: re-running `loadData` would re-resolve the query, the
   * entity types, the permissions and the lookups, and reset the user's page and
   * sort on every button press.
   */
  reloadToken = input<unknown>(null);

  /**
   * Render without the surrounding card, for a host that owns its own chrome — a tab
   * body, a modal, a dashboard tile.
   *
   * This is the escape hatch for a genuinely chromeless embed. It only serves a host
   * that instantiated this component by hand — where the component is auto-rendered
   * from `EntityTypeDefinition.Queries` there is no host to pass it.
   */
  showCard = input(true);

  /**
   * Replace the header for one hand-instantiated usage.
   *
   * A `TemplateRef` rather than `<ng-content>` deliberately: it matches
   * `spark-po-detail`'s `extraActionsTemplate`/`extraContentTemplate`, and unlike
   * projection it can be forwarded by a host that is itself several layers up.
   *
   * Precedence is headerTemplate -> caption + query actions.
   */
  headerTemplate = input<TemplateRef<{ $implicit: SparkQuery }> | null>(null);

  colors = Color;

  query = signal<SparkQuery | null>(null);
  entityType = signal<EntityType | null>(null);
  allEntityTypes = signal<EntityType[]>([]);
  /**
   * Why the component renders its own failure instead of only reporting one.
   *
   * `SparkService` is a bare `firstValueFrom` passthrough with no interceptor, so
   * every failure surfaces here and nowhere else. A host embedding this grid cannot
   * surface what it never sees, and the default has to be visible with no host
   * cooperation — hence a rendered alert, not just an output.
   *
   * A 404 is deliberately vague. `Endpoints/Queries/Get.cs` answers 404 for BOTH
   * "no such query" and "you may not see it", with byte-identical bodies, so that
   * existence is not disclosed (security audit M-3). This component therefore
   * genuinely cannot tell the two apart, and any message claiming otherwise would
   * either leak or mislead.
   */
  errorMessage = signal<string | null>(null);

  /**
   * Actions the query declares, rendered in this component's own header.
   *
   * This is what makes a query's chrome work with no host: a sub-query is rendered
   * automatically from `EntityTypeDefinition.Queries`, so there is nobody to project
   * a toolbar in. The query says what belongs in its header, and it follows the query
   * wherever it is rendered.
   */
  customActions = signal<CustomActionDefinition[]>([]);

  /** Emitted whenever a load or a page fetch fails, for a host in bespoke chrome. */
  error = output<HttpErrorResponse>();
  lookupReferenceOptions = signal<Record<string, LookupReference>>({});
  loading = signal(true);
  canRead = signal(false);
  settings = signal(new DatatableSettings({
    perPage: { values: SPARK_GRID_PAGE_SIZES, selected: SPARK_GRID_PAGE_SIZES[0] },
    page: { values: [1], selected: 1 },
    sortColumns: []
  }));
  fetchFn = signal<BsDatatableFetch<PersistentObject> | null>(null);
  /**
   * Rows the user has ticked. Lives here rather than in the datatable so the action bar can
   * read it, and MUST be cleared whenever the source changes — otherwise route A's selection
   * is POSTed as ids of route B's type.
   */
  selection = signal<PersistentObject[]>([]);

  private readonly queryRefresh = inject(SparkQueryRefreshService);

  /** 'none' unless an action is selection-gated, so unaffected grids gain no checkbox column. */
  selectionMode = computed(() => selectionModeFor(this.customActions()));

  /** Whether an action's selection rule is satisfied right now. The server checks it again. */
  isActionEnabled(action: CustomActionDefinition): boolean {
    return parseSelectionRule(action.selectionRule)(this.selection().length);
  }


  isVirtualScrolling = computed(() => isVirtualScrollingQuery(this.query()));

  visibleAttributes = computed(() => visibleGridAttributes(this.entityType()));

  constructor() {
    effect(() => {
      const qId = this.queryId();
      const pId = this.parentId();
      const pType = this.parentType();
      // Only the query id is required. Requiring a parent here is what made a
      // standalone grid silently render nothing.
      if (qId) {
        this.loadData(qId, pId, pType);
      } else {
        // No query id at all. `loading` starts true, so without this the component
        // would spin forever instead of saying anything.
        this.loading.set(false);
      }
    });

    // Separate effect, so the token drives the cheap refresh and never the full
    // metadata reload. `first` skips the initial run: the effect above has already
    // fetched, and reacting to the token's starting value would double-fetch on mount.
    let first = true;
    effect(() => {
      // Both the host's token and the server's refreshQuery drive the same cheap refresh.
      this.reloadToken();
      this.queryRefresh.tokenFor(this.queryId());
      if (first) { first = false; return; }
      untracked(() => this.reload());
    });
  }

  /**
   * Re-run the query, keeping the current page, sort and scroll position.
   *
   * Data-level on purpose: it re-seeds the fetch closure and nothing else, mirroring
   * `SparkQueryListComponent.reload()`. Use it after something mutates server-side
   * state the query reads from. For a definition change — new columns, a renamed
   * query — the inputs themselves must change; that is the expensive path.
   */
  async onCustomAction(action: CustomActionDefinition): Promise<void> {
    if (action.confirmationMessageKey) {
      const message = this.lang.t(action.confirmationMessageKey) || 'Are you sure?';
      if (!confirm(message)) return;
    }
    try {
      await this.sparkService.executeCustomAction(this.entityType()!.id, action.name, undefined, this.selection());
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
   * `Endpoints/Queries/Get.cs` answers 404 with byte-identical bodies for "no such
   * query" and "you may not see it", so existence is not disclosed (audit M-3). The
   * component therefore cannot tell them apart, and both "Not found" and "Access
   * denied" would be a guess — one of them leaking, the other misleading.
   */
  private describe(err: HttpErrorResponse): string {
    if (err?.status === 404) {
      return this.lang.t('spark.query.unavailable') || 'This list is not available.';
    }
    return err?.error?.error || err?.message
      || this.lang.t('common.unexpectedError') || 'An unexpected error occurred';
  }

  reload(): void {
    const q = this.query();
    if (q) this.fetchFn.set(this.makeFetch(q, this.parentId(), this.parentType()));
  }

  private async loadData(queryId: string, parentId: string, parentType: string): Promise<void> {
    this.loading.set(true);
    this.errorMessage.set(null);
    this.fetchFn.set(null);
    // Reset everything derived from the previous query, not just the fetch. Leaving
    // `entityType`/`canRead` behind let a failed reload build a row link out of the
    // PREVIOUS type and the previous permission.
    this.query.set(null);
    this.entityType.set(null);
    this.canRead.set(false);
    this.customActions.set([]);
    this.selection.set([]);
    try {
      const [resolvedQuery, entityTypes] = await Promise.all([
        this.sparkService.getQuery(queryId),
        this.sparkService.getEntityTypes()
      ]);

      this.query.set(resolvedQuery);
      this.allEntityTypes.set(entityTypes);

      // Resolve entity type from query's entityType field
      if (resolvedQuery.entityType) {
        const et = entityTypes.find(t =>
          t.name === resolvedQuery.entityType || t.alias === resolvedQuery.entityType?.toLowerCase()
        );
        this.entityType.set(et || null);
        if (et) {
          const [permissions, actions] = await Promise.all([
            this.sparkService.getPermissions(et.id),
            this.sparkService.getCustomActions(et.id),
          ]);
          this.canRead.set(permissions.canRead);
          this.customActions.set(filterQueryActions(actions));
        }
      }

      this.settings.set(initialGridSettings(resolvedQuery));
      // The datatable drives paging/sorting via [(settings)] and calls fetchFn
      // per page. Virtual scrolling is just the [virtualScroll] template flag.
      this.fetchFn.set(this.makeFetch(resolvedQuery, parentId, parentType));

      this.loadLookupReferenceOptions();
    } catch (e) {
      this.fetchFn.set(null);
      this.reportError(e as HttpErrorResponse);
    } finally {
      this.loading.set(false);
    }
  }

  private makeFetch(query: SparkQuery, parentId: string, parentType: string): BsDatatableFetch<PersistentObject> {
    return (req) => this.sparkService.executeQuery(query.id, {
      sortColumns: req.sortColumns,
      skip: (req.page - 1) * req.perPage,
      take: req.perPage,
      parentId, parentType,
    }).then(r => {
      this.errorMessage.set(null);
      return {
        data: r.data,
        totalRecords: r.totalRecords,
        totalPages: Math.ceil(r.totalRecords / req.perPage) || 1,
        perPage: req.perPage,
        page: req.page,
      };
    }).catch((e: HttpErrorResponse) => {
      // Report before returning the empty page, or a failed fetch is indistinguishable
      // from a query that legitimately has no rows.
      this.reportError(e);
      return { data: [], totalRecords: 0, totalPages: 1, perPage: req.perPage, page: req.page };
    });
  }

  private async loadLookupReferenceOptions(): Promise<void> {
    this.lookupReferenceOptions.set(await this.gridRenderers.loadLookupOptions(this.visibleAttributes()));
  }

  getColumnRendererComponent(attr: EntityAttributeDefinition): Type<any> | null {
    return this.gridRenderers.columnComponentFor(attr);
  }

  getColumnRendererInputs(component: Type<any>, item: PersistentObject, attr: EntityAttributeDefinition): Record<string, any> {
    return this.gridRenderers.columnInputsFor(component, item, attr);
  }
}
