import { InjectionToken, Provider, Type } from '@angular/core';
import {
  EntityPermissions,
  EntityType,
  PersistentObject,
  SparkDeletedFilter,
  SparkQuery,
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
   * The optional add-on flags (`canRestore`, `canViewHistory`, …) are absent on an older server.
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
