import { InjectionToken, Provider, Type } from '@angular/core';
import {
  EntityPermissions,
  EntityType,
  PersistentObject,
  SparkDeletedFilter,
  QueryResultItem,
  SparkQuery,
  SparkSaveSubmitted,
} from '@mintplayer/ng-spark/models';

/**
 * What a detail-page extension is told about the object on screen (#460).
 *
 * Handed to every {@link SPARK_DETAIL_PANELS} and {@link SPARK_DETAIL_ACTIONS} component through its
 * `context` input. A new object is passed whenever the page's state changes (a reload, a patch), so a
 * component reads it as a plain value and never needs to subscribe to anything.
 */
export interface SparkDetailContext {
  /** The entity type id or alias from the route (`/po/:type/:id`). */
  type: string;
  /** The object id from the route. */
  id: string;
  item: PersistentObject;
  entityType: EntityType;
  /**
   * Type-level rights (`/spark/permissions/{type}`). `null` while they load — treat as "not allowed".
   * Every flag, the add-ons' (`canRestore`, `canViewHistory`, …) included, is always sent.
   */
  permissions: EntityPermissions | null;
  /**
   * The soft-deletion mode the object was loaded with — the route's `?deleted=` query parameter
   * (`include` / `only`), or `null` for a normal load. `only` means the object was opened from the
   * recycle bin and is a deleted row.
   */
  deleted: SparkDeletedFilter | null;
  /** Re-fetches the object (and its permissions) with the same mode. */
  reload(): Promise<void>;
}

/**
 * What a query-page extension is told about the list on screen (#460).
 *
 * Handed to every {@link SPARK_QUERY_LIST_ACTIONS} component through its `context` input.
 */
export interface SparkQueryListContext {
  query: SparkQuery;
  /** Null for a query whose rows are not an entity type (a composed query). */
  entityType: EntityType | null;
  /** Type-level rights of `entityType`; null while they load or when there is no entity type. */
  permissions: EntityPermissions | null;
  /** The soft-deletion mode the list currently runs with (`exclude` unless the route says otherwise). */
  deleted: SparkDeletedFilter;
  /**
   * Switches the list's soft-deletion mode. Written to the route as `?deleted=`, so the recycle bin
   * survives a reload and the back button; `exclude` removes the parameter.
   */
  setDeleted(mode: SparkDeletedFilter): void;
  /** Re-runs the query, keeping page and sort. */
  reload(): void;
}

/**
 * A component shown under the attributes of every routed detail page (`sparkRoutes()`'s
 * `po/:type/:id`). It must declare `context = input.required<SparkDetailContext>()`.
 *
 * `sparkRoutes()` passes no templates to the pages it routes to, so a multi-provider is the only way
 * an add-on (History, Moderation) can put something on them without the app replacing the page.
 */
export interface SparkDetailPanel {
  /** Unique name; a second registration with the same id replaces the first. */
  id: string;
  component: Type<unknown>;
  /** Ascending; panels without an order come last, in registration order. */
  order?: number;
}

/**
 * A component rendered in the action bar of every routed detail page, after the built-in and custom
 * actions. It must declare `context = input.required<SparkDetailContext>()` and render nothing when
 * it does not apply (it is responsible for its own permission checks).
 */
export interface SparkDetailAction {
  id: string;
  component: Type<unknown>;
  /** Priority in the action bar's overflow ordering; defaults to 60. Lower stays visible longer. */
  priority?: number;
}

/**
 * A component rendered in the action bar of every routed query page (`query/:queryId`, `po/:type`).
 * It must declare `context = input.required<SparkQueryListContext>()`.
 */
export interface SparkQueryListAction {
  id: string;
  component: Type<unknown>;
  /** Priority in the action bar's overflow ordering; defaults to 60. */
  priority?: number;
}

/** What a grid row action (`SPARK_QUERY_ROW_ACTIONS`) is asked whether it is offered. */
export interface SparkQueryRowActionScope {
  query: SparkQuery;
  entityType: EntityType;
  permissions: EntityPermissions | null;
  /** The grid's soft-deletion mode (null when the grid sends none). */
  deleted: SparkDeletedFilter | null;
}

/** What a grid row action runs with: the scope, the one row, and the grid's refresh. */
export interface SparkQueryRowActionContext extends SparkQueryRowActionScope {
  row: QueryResultItem;
  /** Re-runs the grid's query (page, sort and filters kept). */
  reload(): void;
}

/**
 * An add-on entry of a Spark grid's per-row `⋮` menu (the query page and the sub-query card): an
 * action an add-on package serves on its own route, so it is not in the type's custom-action
 * catalogue (e.g. Contributions' "Revert to this version").
 *
 * `run` executes in the grid's injection context, so it may `inject()` synchronously before its first
 * `await`. A rejection is shown in the grid's alert (the server's message when it sent one).
 */
export interface SparkQueryRowAction {
  id: string;
  /** Translation key of the menu label. */
  labelKey: string;
  /** Order among the row menu's entries (custom actions are 10+); defaults to 50. */
  priority?: number;
  isOffered(scope: SparkQueryRowActionScope): boolean;
  run(context: SparkQueryRowActionContext): Promise<void>;
}

/** Multi-provider: add-on entries of every Spark grid's row menu. */
export const SPARK_QUERY_ROW_ACTIONS = new InjectionToken<SparkQueryRowAction[]>('SparkQueryRowActions');

/** Registers grid row actions (multi — every call adds). */
export function provideSparkQueryRowActions(...actions: SparkQueryRowAction[]): Provider[] {
  return actions.map(action => ({ provide: SPARK_QUERY_ROW_ACTIONS, useValue: action, multi: true }));
}

/**
 * A layout that draws a detail page's attributes in place of the tabs and cards (#504), for objects it
 * `matches`: Dashboards' tile layout for objects carrying `metadata.dashboard`. It must declare
 * `context = input.required<SparkDetailContext>()`. The action bar, sub-queries, the extra content
 * template and the detail panels stay; the first matching layout wins.
 */
export interface SparkDetailLayout {
  id: string;
  component: Type<unknown>;
  /** Whether this layout draws `item`. Called on every change of the object, so keep it cheap. */
  matches(item: PersistentObject, entityType: EntityType): boolean;
}

/** Multi-provider: layouts that replace the attribute cards of the routed detail page for the objects they match. */
export const SPARK_DETAIL_LAYOUTS = new InjectionToken<SparkDetailLayout[]>('SparkDetailLayouts');

/** Registers detail layouts (multi — every call adds). */
export function provideSparkDetailLayouts(...layouts: SparkDetailLayout[]): Provider[] {
  return layouts.map(layout => ({ provide: SPARK_DETAIL_LAYOUTS, useValue: layout, multi: true }));
}

/** What a {@link SparkDetailTitleBadge} draws: a badge after the routed detail page's title. */
export interface SparkTitleBadgeView {
  /** Translation key of the badge text. */
  labelKey: string;
  /** A bootstrap colour name (`secondary`, `warning`, `info`, …); defaults to `secondary`. */
  color?: string;
  /** Extra classes on the badge, for tests and styling. */
  cssClass?: string;
  /** A router link: the badge becomes a link (e.g. to the pending approval request). */
  link?: unknown[];
}

/**
 * A badge after the title of the routed detail page (commerce #521): core's "Deleted" in the recycle
 * bin, Approvals' "Approval pending". `badge` is called whenever the page's context changes and answers
 * `null` when it does not apply, so keep it cheap.
 */
export interface SparkDetailTitleBadge {
  /** Unique name; a second registration with the same id replaces the first (`spark-deleted` is core's). */
  id: string;
  /** Ascending; badges without an order come last, in registration order. */
  order?: number;
  badge(context: SparkDetailContext): SparkTitleBadgeView | null;
}

/** Multi-provider: badges after the routed detail page's title. */
export const SPARK_DETAIL_TITLE_BADGES = new InjectionToken<SparkDetailTitleBadge[]>('SparkDetailTitleBadges');

/** Registers detail title badges (multi — every call adds). */
export function provideSparkDetailTitleBadges(...badges: SparkDetailTitleBadge[]): Provider[] {
  return badges.map(badge => ({ provide: SPARK_DETAIL_TITLE_BADGES, useValue: badge, multi: true }));
}

/**
 * The badges every detail page draws without registration: "Deleted" on a row opened from the recycle bin.
 * An app (or add-on) registering the same id replaces it.
 */
export const sparkCoreTitleBadges: readonly SparkDetailTitleBadge[] = [
  {
    id: 'spark-deleted',
    order: 0,
    badge: context => context.deleted === 'only'
      ? { labelKey: 'softDelete.deleted', color: 'secondary', cssClass: 'spark-deleted-badge' }
      : null,
  },
];

/** The title badges that apply to `context`, core's first, each id once, in order. */
export function sparkTitleBadgesFor(
  registered: readonly SparkDetailTitleBadge[] | null | undefined,
  context: SparkDetailContext | null,
): (SparkTitleBadgeView & { id: string })[] {
  if (!context) return [];
  return orderSparkExtensions([...sparkCoreTitleBadges, ...(registered ?? [])], b => b.order)
    .map(b => {
      const view = b.badge(context);
      return view ? { ...view, id: b.id } : null;
    })
    .filter((v): v is SparkTitleBadgeView & { id: string } => v !== null);
}

/**
 * What a create or edit page does after a save the server diverted (202, commerce X7): asked in
 * registration order; the first non-null route wins, and `null` from every handler keeps the page's own
 * behaviour (create: back to where the New started; edit: the detail page). Approvals sends a diverted
 * creation to its approval request, since the object itself does not exist yet (opening it would 404).
 */
export interface SparkSaveSubmittedContext {
  operation: 'create' | 'update';
  /** The entity type id or alias from the route. */
  type: string;
  /** The object id, for an update. */
  id?: string;
  submitted: SparkSaveSubmitted;
}

export type SparkSaveSubmittedHandler = (context: SparkSaveSubmittedContext) => unknown[] | null;

/** Multi-provider: where to go after a diverted save. See {@link SparkSaveSubmittedContext}. */
export const SPARK_SAVE_SUBMITTED_HANDLERS = new InjectionToken<SparkSaveSubmittedHandler[]>('SparkSaveSubmittedHandlers');

/** Registers diverted-save handlers (multi — every call adds). */
export function provideSparkSaveSubmittedHandlers(...handlers: SparkSaveSubmittedHandler[]): Provider[] {
  return handlers.map(handler => ({ provide: SPARK_SAVE_SUBMITTED_HANDLERS, useValue: handler, multi: true }));
}

/** The route the first handler answers for `context`, or null to keep the page's own behaviour. */
export function sparkSaveSubmittedRoute(
  handlers: readonly SparkSaveSubmittedHandler[] | null | undefined,
  context: SparkSaveSubmittedContext,
): unknown[] | null {
  for (const handler of handlers ?? []) {
    const route = handler(context);
    if (route) return route;
  }
  return null;
}

/** Multi-provider: panels under the attributes of the routed detail page. */
export const SPARK_DETAIL_PANELS = new InjectionToken<SparkDetailPanel[]>('SparkDetailPanels');

/** Multi-provider: extra buttons in the routed detail page's action bar. */
export const SPARK_DETAIL_ACTIONS = new InjectionToken<SparkDetailAction[]>('SparkDetailActions');

/** Multi-provider: extra buttons in the routed query page's action bar. */
export const SPARK_QUERY_LIST_ACTIONS = new InjectionToken<SparkQueryListAction[]>('SparkQueryListActions');

/** Registers detail panels (multi — every call adds). */
export function provideSparkDetailPanels(...panels: SparkDetailPanel[]): Provider[] {
  return panels.map(panel => ({ provide: SPARK_DETAIL_PANELS, useValue: panel, multi: true }));
}

/** Registers detail-page actions (multi — every call adds). */
export function provideSparkDetailActions(...actions: SparkDetailAction[]): Provider[] {
  return actions.map(action => ({ provide: SPARK_DETAIL_ACTIONS, useValue: action, multi: true }));
}

/** Registers query-page actions (multi — every call adds). */
export function provideSparkQueryListActions(...actions: SparkQueryListAction[]): Provider[] {
  return actions.map(action => ({ provide: SPARK_QUERY_LIST_ACTIONS, useValue: action, multi: true }));
}

/**
 * Normalises a multi-provided list: the last registration of an id wins (so an app can replace a
 * shipped panel by registering its own under the same id), then sorted by `key` ascending with
 * unordered entries last, keeping registration order among equals.
 */
export function orderSparkExtensions<T extends { id: string }>(
  entries: readonly T[] | null | undefined,
  key: (entry: T) => number | undefined,
): T[] {
  if (!entries?.length) return [];

  const byId = new Map<string, { entry: T; index: number }>();
  entries.forEach((entry, index) => {
    const previous = byId.get(entry.id);
    // Replacement keeps the ORIGINAL slot, so overriding a shipped panel does not move it.
    byId.set(entry.id, { entry, index: previous?.index ?? index });
  });

  return [...byId.values()]
    .sort((a, b) => {
      const ka = key(a.entry) ?? Number.POSITIVE_INFINITY;
      const kb = key(b.entry) ?? Number.POSITIVE_INFINITY;
      return ka === kb ? a.index - b.index : ka - kb;
    })
    .map(x => x.entry);
}

/** Reads `?deleted=` into a filter, ignoring anything that is not one of the three modes. */
export function parseSparkDeletedParam(value: string | null | undefined): SparkDeletedFilter | null {
  switch (value?.toLowerCase()) {
    case 'include': return 'include';
    case 'only': return 'only';
    case 'exclude': return 'exclude';
    default: return null;
  }
}
