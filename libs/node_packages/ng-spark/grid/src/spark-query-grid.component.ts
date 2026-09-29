import { ChangeDetectionStrategy, Component, computed, effect, inject, input, model, output, signal, untracked, Type } from '@angular/core';
import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterModule } from '@angular/router';
import { filter, map } from 'rxjs';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsDatatableComponent, BsDatatableColumnDirective, BsDatatableFilterPanelDirective, BsRowTemplateDirective, DatatableSettings, type BsDatatableFetch, type BsDatatableRowEvent, type DatatableDistincts, type FilterChangeDetail } from '@mintplayer/ng-bootstrap/datatable';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
// ⚠️ NOT @mintplayer/ng-bootstrap/dropdown. Its fesm (22.19.0) declares BsDropdownToggleDirective,
// whose factory lists BsDropdownDirective as an eager dependency, BEFORE BsDropdownDirective. Linked
// (an app build) that is harmless; evaluated unlinked — every vitest / unit-test-builder spec that
// imports this grid, in ng-spark and in every app — it throws "Cannot access 'BsDropdownDirective'
// before initialization" at import time (#460 M15, CI run a1b47012). The row menu therefore drives
// the CDK overlay itself and keeps ng-bootstrap's menu for the look.
import { CdkConnectedOverlay, CdkOverlayOrigin, type ConnectedPosition } from '@angular/cdk/overlay';
import { BsDropdownItemDirective, BsDropdownMenuComponent } from '@mintplayer/ng-bootstrap/dropdown-menu';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';
import { SparkQueryRefreshService } from '@mintplayer/ng-spark/client-operations';
import { cellValue } from '@mintplayer/ng-spark/renderers';
import { QueryCellValuePipe, QueryReferenceChipsPipe, ResolveTranslationPipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkAttributeDescriptionComponent } from '@mintplayer/ng-spark/attribute-description';
import { SPARK_RETURN_URL_STATE_KEY, SparkLanguageService, SparkService } from '@mintplayer/ng-spark/services';
import { SparkColumnFilterPanelComponent } from '@mintplayer/ng-spark/column-filter';
import {
  CustomActionDefinition,
  EntityAttributeDefinition,
  EntityType,
  LookupReference,
  QueryColumn,
  QueryColumnFilter,
  QueryResultItem,
  SparkQuery,
  EntityPermissions,
  SparkDeletedFilter,
  filterQueryActions,
  defaultQueryActions,
  type SparkSelectionModeSetting,
  parseSelectionRule,
  selectionModeFor,
  valueFor,
} from '@mintplayer/ng-spark/models';
import { SPARK_GRID_PAGE_SIZES, initialGridSettings, isVirtualScrollingQuery } from './spark-grid-columns';
import { SparkGridRenderers } from './spark-grid-renderers';
import { SparkGridCellComponent } from './spark-grid-cell.component';
import { SparkQueryToolbarAction, sparkActionClass } from './spark-query-toolbar';

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
  imports: [CommonModule, RouterModule, BsAlertComponent, BsDatatableComponent, BsDatatableColumnDirective, BsDatatableFilterPanelDirective, BsRowTemplateDirective, BsSpinnerComponent, SparkGridCellComponent, ResolveTranslationPipe, QueryCellValuePipe, QueryReferenceChipsPipe, TranslateKeyPipe, SparkAttributeDescriptionComponent, SparkColumnFilterPanelComponent, BsBadgeComponent, CdkOverlayOrigin, CdkConnectedOverlay, BsDropdownMenuComponent, BsDropdownItemDirective, SparkIconComponent],
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
   * Soft-deletion mode (#460, T2): `exclude` (the default, not sent), `include` or `only` (the
   * recycle bin). Sent with every page and distinct-values request; the server honours a widening
   * only for `ViewDeleted` holders. A change goes back to page 1 and refetches, like a filter.
   * While set, row links carry `?deleted=` so the detail page loads the row the same way — a deleted
   * row is a 404 by id otherwise.
   */
  deleted = input<SparkDeletedFilter | null | undefined>(null);

  /** The mode actually sent: `exclude` and "unset" are the same request. */
  protected readonly effectiveDeleted = computed(() => {
    const mode = this.deleted();
    return mode && mode !== 'exclude' ? mode : undefined;
  });

  /** Query parameters for the row links; null keeps a plain link. */
  protected readonly rowQueryParams = computed(() => {
    const mode = this.effectiveDeleted();
    return mode ? { deleted: mode } : null;
  });

  private readonly router = inject(Router);
  private readonly currentUrl = toSignal(
    this.router.events.pipe(filter(e => e instanceof NavigationEnd), map(() => this.router.url)),
    { initialValue: this.router.url });

  /**
   * Navigation state for a row link: this page's URL, so the detail page can return to this list
   * (with its `?deleted=` mode) once the row is deleted or purged. See SparkReturnNavigationService.
   */
  protected readonly rowLinkState = computed(() => ({ [SPARK_RETURN_URL_STATE_KEY]: this.currentUrl() }));

  /**
   * A click anywhere on a row opens it, like its first-column link — the link used to be the only
   * target, so a row whose first column was empty ("—") could not be opened at all. While rows are
   * selectable a click selects instead, and a double-click opens. Clicks on the link itself or on an
   * interactive element inside the row (a vote button, a checkbox) are theirs, not the row's; a
   * modified click (Ctrl, ⌘, Shift, middle) is left to the browser.
   */
  protected onRowClick(event: BsDatatableRowEvent<unknown>): void {
    if (this.selectionMode() === 'none') this.openRow(event);
  }

  protected onRowDblClick(event: BsDatatableRowEvent<unknown>): void {
    if (this.selectionMode() !== 'none') this.openRow(event);
  }

  private openRow(event: BsDatatableRowEvent<unknown>): void {
    const row = event.row as QueryResultItem | null;
    const original = event.originalEvent as MouseEvent | undefined;
    const target = original?.target as Element | null | undefined;
    if (target?.closest?.('a, button, input, select, textarea, label, [role="button"]')) return;
    if (original && (original.ctrlKey || original.metaKey || original.shiftKey || original.button === 1)) return;
    if (!row || !this.canRead()) return;
    const route = this.rowRouteFor(row);
    if (!route) return;
    this.rowClicked.emit(row);
    void this.router.navigate(route, { queryParams: this.rowQueryParams() ?? undefined, state: this.rowLinkState() });
  }

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

  /**
   * Overrides the query's own `selectionMode` (#460, D17) — the detail page passes the sub-query
   * entry's. `null` (the default) defers to the query, whose absent setting is `'auto'`.
   */
  selectionModeSetting = input<SparkSelectionModeSetting | null>(null);

  /** Emitted whenever a load or a page fetch fails, for a host in bespoke chrome. */
  error = output<HttpErrorResponse>();
  rowClicked = output<QueryResultItem>();
  customActionExecuted = output<{ action: CustomActionDefinition }>();
  /** Emitted when the toolbar's New is pressed, just before the grid navigates to the create page. */
  createClicked = output<void>();

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
  /** The entity type's full rights, for hosts (the query page hands them to its add-on actions). */
  permissions = signal<EntityPermissions | null>(null);
  resultCount = signal<number | null>(null);
  customActions = signal<CustomActionDefinition[]>([]);
  /** The built-in New and Delete the server listed for this caller (#460, D18); see `toolbarActions`. */
  defaultActions = signal<CustomActionDefinition[]>([]);

  /** Names the server withheld for this RESULT; see QueryResult.disabledActions. */
  private readonly disabledActions = signal<string[]>([]);

  /**
   * The actions actually offered for this result.
   *
   * The catalogue at `/spark/actions/{objectTypeId}` is per TYPE and is never told what an
   * execution returned, so an action that applies to only some results cannot be filtered there.
   * The entity's `OnDisableActionsAsync` hook withholds what does not apply to this query (#460,
   * D13), and the answer arrives on the result. The server asks the same hook when an action is
   * submitted from this query and refuses a disabled one with 403.
   */
  /**
   * Whether to offer "New": the type-level right, unless the result withholds `New` (or `Save`) --
   * the server's OnDisableActionsAsync said so and would refuse the create with 403 (#460, D13).
   * `canCreate` stays the bare right, since hosts read it as exactly that.
   */
  offersCreate = computed(() => {
    if (!this.canCreate()) return false;
    const withheld = this.disabledActions().map(name => name.toLowerCase());
    return !withheld.includes('new') && !withheld.includes('save');
  });

  visibleCustomActions = computed(() => {
    const withheld = this.disabledActions();
    if (!withheld.length) return this.customActions();

    const lowered = new Set(withheld.map(name => name.toLowerCase()));
    return this.customActions().filter(action => !lowered.has(action.name.toLowerCase()));
  });
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

  /**
   * The rendered selection mode (#460, D17): the host's override (a sub-query entry's
   * `selectionMode`), else the query's own, else `'auto'` — derived from the CUSTOM actions offered,
   * exactly as before the setting existed, so no existing grid changes behaviour. The default Delete
   * does not widen `'auto'`: selection is opt-in, and without it Delete stays reachable per row
   * through the row menu.
   */
  selectionMode = computed(() => selectionModeFor(
    this.visibleCustomActions(),
    this.selectionModeSetting() ?? this.query()?.selectionMode ?? 'auto'));

  /**
   * The actions a host renders in its toolbar, in one list (#460, M15): New, Delete and the custom
   * actions, each with the rule that enables it. The query card and the query-list page render this
   * same list, so the two surfaces cannot drift.
   *
   * - New: the default entry's `showedOn` includes the query, the caller holds `New/T`, and the
   *   result does not withhold `New`/`Save`.
   * - Delete: likewise with `Delete/T` and `Delete`, and only while rows can be selected (otherwise
   *   it could never be enabled — the row menu offers it per row instead).
   * - Custom: `visibleCustomActions()`.
   */
  toolbarActions = computed((): SparkQueryToolbarAction[] => {
    const actions: SparkQueryToolbarAction[] = [];
    const withheld = new Set(this.disabledActions().map(name => name.toLowerCase()));

    for (const definition of this.defaultActions()) {
      const name = definition.name.toLowerCase();
      if (name === 'new' && this.offersCreate()) {
        actions.push({ kind: 'new', name: definition.name, definition, priority: 1 + definition.offset });
      } else if (name === 'delete' && !withheld.has('delete') && this.selectionMode() !== 'none') {
        actions.push({ kind: 'delete', name: definition.name, definition, priority: 5 + definition.offset });
      }
    }

    for (const definition of this.visibleCustomActions())
      actions.push({ kind: 'custom', name: definition.name, definition, priority: 10 + definition.offset });

    return actions;
  });

  /**
   * The per-row `⋮` menu: every offered action whose rule accepts exactly one row — Delete included,
   * whatever the selection mode. An action without a rule acts on the query, not on a row, and is
   * left to the toolbar.
   */
  rowActions = computed((): SparkQueryToolbarAction[] => {
    const withheld = new Set(this.disabledActions().map(name => name.toLowerCase()));
    const rowTaking = (definition: CustomActionDefinition) =>
      !!definition.selectionRule?.trim() && parseSelectionRule(definition.selectionRule)(1);

    const actions: SparkQueryToolbarAction[] = [];
    for (const definition of this.defaultActions()) {
      if (definition.name.toLowerCase() === 'delete' && !withheld.has('delete') && rowTaking(definition))
        actions.push({ kind: 'delete', name: definition.name, definition, priority: 5 + definition.offset });
    }
    for (const definition of this.visibleCustomActions()) {
      if (rowTaking(definition))
        actions.push({ kind: 'custom', name: definition.name, definition, priority: 10 + definition.offset });
    }
    return actions;
  });

  /** Whether a toolbar action can run with the current selection. The server checks again. */
  isToolbarActionEnabled(action: SparkQueryToolbarAction): boolean {
    return this.isActionEnabled(action.definition);
  }

  /** Runs a toolbar action on the current selection. */
  async runToolbarAction(action: SparkQueryToolbarAction): Promise<void> {
    switch (action.kind) {
      case 'new':
        this.startNew();
        return;
      case 'delete':
        await this.deleteRows(this.selection().map(r => r.id), action.definition);
        return;
      default:
        await this.onCustomAction(action.definition);
    }
  }

  /** The id of the row whose `⋮` menu is open; at most one is. */
  readonly openRowMenu = signal<string | null>(null);

  /** Below the toggle, right-aligned; above it when there is no room below. */
  readonly rowMenuPositions: ConnectedPosition[] = [
    { originX: 'end', originY: 'bottom', overlayX: 'end', overlayY: 'top' },
    { originX: 'end', originY: 'top', overlayX: 'end', overlayY: 'bottom' },
  ];

  toggleRowMenu(row: QueryResultItem): void {
    this.openRowMenu.update(open => open === row.id ? null : row.id);
  }

  /**
   * Closes the open row menu. With a `rowId`, only when that row's menu is the open one: a row's
   * overlay reports its outside click and its detach AFTER another row's toggle has already opened
   * that row's menu, and an unscoped close there shut the menu the user had just opened.
   */
  closeRowMenu(rowId?: string): void {
    if (rowId !== undefined && this.openRowMenu() !== rowId) return;
    this.openRowMenu.set(null);
  }

  /**
   * A click outside the open menu closes it — except on its own toggle, whose click handler toggles
   * it; closing here as well would reopen it at once.
   */
  onRowMenuOutsideClick(event: MouseEvent, toggle: HTMLElement, rowId: string): void {
    if (event.target instanceof Node && toggle.contains(event.target)) return;
    this.closeRowMenu(rowId);
  }

  /** Runs the chosen row-menu item and closes the menu. */
  async chooseRowAction(action: SparkQueryToolbarAction, row: QueryResultItem): Promise<void> {
    this.closeRowMenu();
    await this.runRowAction(action, row);
  }

  /**
   * Runs a row-menu action on that ONE row. The checkbox selection is neither read nor changed:
   * the menu is a shortcut for this row, not a second way to tick it.
   */
  async runRowAction(action: SparkQueryToolbarAction, row: QueryResultItem): Promise<void> {
    if (action.kind === 'delete') {
      await this.deleteRows([row.id], action.definition);
      return;
    }
    await this.onCustomAction(action.definition, [row.id]);
  }

  /**
   * New from this grid: the create page, carrying the sub-query's parent (#460, D19) as query
   * parameters so it survives the navigation and a reload of the create page. The server's
   * `OnNewAsync` receives the parent; its base fills the parent reference.
   */
  startNew(): void {
    const type = this.entityType();
    if (!type) return;
    this.createClicked.emit();

    const parentId = this.parentId();
    const parentType = this.parentType();
    const query = this.query();
    const queryParams = parentId && parentType && query
      ? { parentId, parentType, queryId: query.alias || query.id }
      : undefined;
    void this.router.navigate(['/po', type.alias || type.id, 'new'], { queryParams, state: this.rowLinkState() });
  }

  /**
   * The default Delete on `ids`: one request, all rows or none (#460, D18). Asks first — with the
   * entry's `confirmationMessageKey` — then clears the selection and refreshes.
   */
  async deleteRows(ids: string[], definition?: CustomActionDefinition): Promise<void> {
    const type = this.entityType();
    if (!type || !ids.length) return;

    const key = definition?.confirmationMessageKey;
    const message = ids.length === 1
      ? this.lang.t('common.confirmDelete')
      : (key ? this.lang.t(key) : '') || this.lang.t('common.confirmDeleteSelected');
    if (!confirm(`${message || 'Are you sure?'}${ids.length > 1 ? ` (${ids.length})` : ''}`)) return;

    const parentId = this.parentId();
    const parentType = this.parentType();
    try {
      await this.sparkService.deleteMany(type.id, ids, {
        queryId: this.query()?.id,
        ...(parentId && parentType ? { parentId, parentType } : {}),
      });
      const removed = new Set(ids);
      this.selection.set(this.selection().filter(r => !removed.has(r.id)));
      this.reload();
    } catch (e) {
      const err = e as HttpErrorResponse;
      this.errorMessage.set(
        err.error?.result?.errors?.[0]?.message
        || err.error?.result?.error
        || err.error?.error
        || (err.status === 404 ? this.lang.t('common.deleteRefused') : '')
        || err.message
        || this.lang.t('common.actionFailed')
        || 'Action failed');
    }
  }

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
    //
    // ⚠️ A new term also CLEARS the selection (#460 M15, D17 addendum), on both surfaces — the card
    // and the query-list page — and in both transports. Reconciling it with the new rows was the
    // alternative and was rejected: the grid only ever holds one page, so a ticked row that is merely
    // on another page of the searched result would be indistinguishable from one the search excluded,
    // and a bulk Delete would then act on rows the user can no longer see. Clearing keeps the
    // "N selected" chip equal to what is ticked on screen, exactly as the soft-deletion mode does.
    let firstSearch = true;
    effect(() => {
      this.search();
      if (firstSearch) { firstSearch = false; return; }
      untracked(() => {
        this.selection.set([]);
        this.onSearchChanged();
      });
    });

    // A different soft-deletion mode is a different result set: page 1, fresh fetch identity. The
    // selection is dropped too — ids ticked in the recycle bin are not rows of the live list.
    let firstDeleted = true;
    effect(() => {
      this.effectiveDeleted();
      if (firstDeleted) { firstDeleted = false; return; }
      untracked(() => {
        this.selection.set([]);
        this.onFilterChanged();
      });
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

  /**
   * Replaces the fetched columns only when they actually differ.
   *
   * Columns ship **once per result**, so every page fetch returns an identical set. Assigning it
   * unconditionally wrote a new array of new objects into the signal each time, re-rendering every
   * header and re-evaluating every `*bsDatatableColumn` input for no change in content.
   *
   * Compared by serialized value rather than by reference: the objects are freshly deserialized from
   * JSON on every response, so reference equality is always false and would defeat the check. A
   * dozen small objects per fetch is nothing beside the request that produced them.
   *
   * ⚠️ This was written believing it fixed a panel-teardown bug (#431 F7). Measured in a browser: it
   * does not. The columns were already identical on every fetch, so the guard hits every time and the
   * teardown had another cause entirely. Kept because avoiding a pointless re-render of every header
   * on every page is worth having on its own — but it is an optimization, not a fix, and the F7
   * question remains open.
   */
  private setFetchedColumns(columns: QueryColumn[]): void {
    const current = this.fetchedColumns();
    if (current.length === columns.length && JSON.stringify(current) === JSON.stringify(columns)) return;

    this.fetchedColumns.set(columns);
  }

  /**
   * The per-column filters currently applied (#431).
   *
   * Read inside `makeFetch`'s closure, like `search`, so a change refetches by producing a new fetch
   * identity rather than by being captured.
   */
  private readonly filters = signal<QueryColumnFilter[]>([]);

  /**
   * The value source behind every column's panel.
   *
   * ⚠️ **Referentially stable, and that is the whole point.** It reads `filters()` when the datatable
   * *calls* it, not when it is built — so it always sees the current filters while never changing
   * identity.
   *
   * This was a `computed` first, on the reasoning that it had to close over the current filters
   * because `DistinctsRequest` carries only `{ column, search, signal }`. That was wrong, and the way
   * it was wrong is worth keeping: a new identity reassigns `[distincts]` on the element, and the
   * component aborts the in-flight request and bumps a generation counter — so a response already on
   * its way is discarded as stale and the list renders empty over a perfectly good answer.
   *
   * Reading the signal at call time removes the race entirely and is simpler besides.
   *
   * The asked-for column's own filter is excluded: a panel must offer the values you could still
   * pick, not only the ones you already picked.
   */
  /**
   * The last complete distinct list seen per column, for translating a `< none >` selection.
   *
   * ⚠️ Only stored when the server reported the list was NOT truncated (`hasMore === false`). The
   * translation below is the complement of what is listed, so a partial list would exclude the wrong
   * set — and silently, which is the failure mode this whole feature exists to remove.
   */
  private readonly completeDistincts = new Map<string, unknown[]>();

  protected readonly distinctsFn: DatatableDistincts = async request => {
    const queryId = this.query()?.id;
    if (!queryId) return null;

    // Read at call time — never captured.
    const others = this.filters().filter(f => f.name !== request.column);

    // The server's shape IS the datatable's shape, so there is nothing to map.
    // The grid's own search too (#460 M15), read at call time like the filters: the values are
    // drawn from the rows the searched grid shows, not from the whole query.
    const querySearch = this.search() || undefined;
    const result = await this.sparkService.getDistinctValues(queryId, request.column, {
      search: request.search,
      querySearch,
      columns: others,
      parentId: this.parentId(),
      parentType: this.parentType(),
      deleted: this.effectiveDeleted(),
    });

    // A searched list is a subset by construction, so it is never a basis for a complement — and
    // that holds for the grid's search as much as for the panel's own.
    if (result && !result.hasMore && !request.search && !querySearch)
      this.completeDistincts.set(request.column, result.matching.map(v => v.value));
    else
      this.completeDistincts.delete(request.column);

    return result;
  };

  /**
   * Translates one filter change into the wire shape and refetches.
   *
   * The event always arrives as the `'values'` shape because the panel only ever calls
   * `ctx.apply(...)`; comparison mode is never declared. `inverse` is a flag on the event and two
   * arrays on the wire — the translation is this method's whole job.
   */
  protected onFilterChange(detail: FilterChangeDetail): void {
    if (detail.mode !== 'values') return;

    const values = detail.selected.map(v => v.value);
    const next = this.filters().filter(f => f.name !== detail.column);

    if (values.length > 0) {
      // ⚠️ `< none >` cannot be asked for directly. The server compares against the index term, and
      // RavenDB gives a field the document never wrote NO term at all — so `Col == null` matches a
      // field written as JSON null but NOT one that is absent, while `Col != null` puts the absent row
      // with the rows that HAVE a value. Both rows render blank, the panel offers one entry for the
      // pair, and no predicate selects that pair. Measured in AbsentVersusNullFieldTests.
      //
      // The complement is expressible, though: "none of the real values" catches absent and null
      // alike. So a selection containing the null entry is sent as an exclusion of everything NOT
      // selected, which is exactly the same set and needs no server change.
      const complete = this.completeDistincts.get(detail.column);
      const selectsNone = values.some(v => v === null || v === undefined);

      if (selectsNone && !detail.inverse && complete) {
        const unselected = complete.filter(v =>
          v !== null && v !== undefined && !values.some(s => s === v));

        // Everything is selected, so the filter narrows nothing — drop it rather than sending an
        // empty exclusion, which would read as "exclude nothing" only by accident.
        if (unselected.length > 0)
          next.push({ name: detail.column, excludes: unselected });
      } else {
        next.push(detail.inverse
          ? { name: detail.column, excludes: values }
          : { name: detail.column, includes: values });
      }
    }

    this.filters.set(next);
    this.onFilterChanged();
  }

  /**
   * Back to page 1 and a fresh fetch identity, exactly as a new search term does.
   *
   * Both halves matter: the old page number means nothing against a different result set, and the
   * datatable dedupes reloads by `(page, perPage, sort)` — none of which a filter changes — so
   * without a new fetch identity the request is silently never made.
   */
  private onFilterChanged(): void {
    if (this.hasExternalData()) return;
    const s = this.settings();
    this.settings.set(new DatatableSettings({
      perPage: { values: s.perPage.values, selected: s.perPage.selected },
      page: { values: [1], selected: 1 },
      sortColumns: s.sortColumns,
    }));
    this.reload();
  }

  /**
   * Runs a custom action on the selection — or on `rowIds` when given (the row menu), which leaves
   * the checkbox selection untouched.
   */
  async onCustomAction(action: CustomActionDefinition, rowIds?: string[]): Promise<void> {
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
        rowIds ?? this.selection().map(r => r.id),
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
    this.permissions.set(null);
    this.customActions.set([]);
    this.defaultActions.set([]);
    this.resultCount.set(null);
    // Ids from the previous query are meaningless against the next one, and would be POSTed as
    // though they belonged to it.
    this.selection.set([]);
    // A filter belongs to the query it was applied to. Surviving a query switch would POST it
    // against the next query, where the column may not exist or may mean something else entirely.
    this.filters.set([]);
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
        this.permissions.set(permissions);
        // 'query', not 'list'. The server model has always documented "detail" | "query" |
        // "both"; a filter testing for a value nothing emits renders the action NOWHERE.
        this.customActions.set(filterQueryActions(actions));
        this.defaultActions.set(defaultQueryActions(actions));
      } else if (resolvedQuery) {
        // Said out loud: with no entity type there are no permissions and no actions to ask for, and
        // a grid that silently offers nothing reads exactly like "this type has no actions" (M15).
        console.warn(`[spark] Query '${resolvedQuery.name}' names entity type '${resolvedQuery.entityType ?? resolvedQuery.source}', `
          + 'which resolves to no type in /spark/types; the grid offers no actions and no row links.');
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
    // `name + 's'` because the "-es" rule over-strips (Vehicles -> "Vehicl", Roles -> "Rol"); the
    // page resolver in spark-query-list has always matched this way.
    return entityTypes.find(t =>
      t.name === sourceName || t.name === singular || t.name + 's' === sourceName || t.clrType?.endsWith(singular)) ?? null;
  }

  private makeFetch(query: SparkQuery, parentId: string, parentType: string): BsDatatableFetch<QueryResultItem> {
    return (req) => this.sparkService.executeQuery(query.id, {
      sortColumns: req.sortColumns,
      skip: (req.page - 1) * req.perPage,
      take: req.perPage,
      search: this.search() || undefined,
      // Read inside the closure, exactly as `search` is: what forces a refetch is the fetch
      // identity `reload()` creates, not the value captured when this closure was built.
      columns: this.filters(),
      parentId, parentType,
      // Only when set, so a plain grid's request body is exactly what it was before #460.
      ...(this.effectiveDeleted() ? { deleted: this.effectiveDeleted() } : {}),
    }).then(r => {
      this.errorMessage.set(null);
      this.resultCount.set(r.totalItems);
      // Columns come from the result now, not from the entity type: the server decides the query
      // surface (ShowedOn.Query) and is the same place the sort-column allow-list is checked.
      // ?? [] because a malformed or older response must render an empty grid, not throw inside a
      // computed — where the stack points at the column filter and not at the response that lacked them.
      this.setFetchedColumns(r.columns ?? []);
      // Per-result, so it is re-read on every page rather than latched from the first.
      this.disabledActions.set(r.disabledActions ?? []);
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

  /** The renderer's optional filter-label override for this column, or null for the server text. */
  filterLabelFor(column: QueryColumn): ((value: unknown) => string) | null {
    return this.gridRenderers.filterLabelFor(column);
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
