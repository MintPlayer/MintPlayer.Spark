import { PersistentObject } from './persistent-object';
import { PersistentObjectAttribute } from './persistent-object-attribute';
import { QueryColumnFilter, QueryResult, QueryResultItem } from './query-result';
import { SparkDeletedFilter } from './spark-query';

/**
 * The live-updates protocol (`/spark/live`, X4), as `docs/guide-live-updates.md` "The protocol" fixes it.
 *
 * One WebSocket per browser tab carries every subscription; one JSON object per text frame. Subscriptions
 * are reads only: mutations stay on HTTP.
 */

/** A subscription to one persistent object: exactly the body of `POST /spark/po/load`. */
export interface LivePoRequest {
  objectTypeId: string;
  id: string;
  deleted?: SparkDeletedFilter | null;
}

/** A sort column as `/spark/queries/execute` takes it. */
export interface LiveSortColumn {
  property: string;
  direction: 'asc' | 'desc';
}

/**
 * A subscription to one page of a query: exactly the body of `POST /spark/queries/execute`
 * (`retryResults` is ignored by the server — a push cannot prompt).
 */
export interface LiveQueryRequest {
  queryId: string;
  skip?: number | null;
  take?: number | null;
  sortColumns?: LiveSortColumn[] | null;
  search?: string | null;
  columns?: QueryColumnFilter[] | null;
  parentId?: string | null;
  parentType?: string | null;
  deleted?: SparkDeletedFilter | null;
  parentDeleted?: SparkDeletedFilter | null;
}

// --- client → server ------------------------------------------------------------------------------

export interface LiveSubscribePoOp { op: 'subscribe'; sub: string; po: LivePoRequest }
export interface LiveSubscribeQueryOp { op: 'subscribe'; sub: string; query: LiveQueryRequest }
/** The same query subscription with a new page, sort, search or filters; the server keeps the sub. */
export interface LiveUpdateOp { op: 'update'; sub: string; query: LiveQueryRequest }
export interface LiveUnsubscribeOp { op: 'unsubscribe'; sub: string }

export type LiveClientOp = LiveSubscribePoOp | LiveSubscribeQueryOp | LiveUpdateOp | LiveUnsubscribeOp;

// --- server → client ------------------------------------------------------------------------------

/** `error` codes. Every one ends its subscription; the socket stays open. */
export type LiveErrorCode = 'unavailable' | 'invalid' | 'limit' | 'failed';

/** The changes to a held persistent object: top-level fields (`null` = removed) and whole attributes, by `name`. */
export interface LivePoPatch {
  set?: Record<string, unknown>;
  attributes?: PersistentObjectAttribute[];
}

/**
 * The changes to a held query page. Apply: index the held rows by `id`; drop `remove`; replace or add each
 * `upsert`; the rows are `order` mapped through the index when present, otherwise the held order minus the
 * removed rows; then assign `set` (`totalItems`, `skip`, `take`, `disabledActions`, `typeHints`, …).
 */
export interface LiveQueryPatch {
  upsert?: QueryResultItem[];
  remove?: string[];
  order?: string[];
  set?: Record<string, unknown>;
}

/** The first state of an object, or one whose set or order of attributes changed. Replace. */
export interface LivePoFrame { type: 'po'; sub: string; po: PersistentObject }
export interface LivePoPatchFrame { type: 'poPatch'; sub: string; patch: LivePoPatch }
/** Deleted or no longer readable (one answer). The subscription has ended. */
export interface LiveGoneFrame { type: 'gone'; sub: string }
/** The first page, a page whose columns changed, or a change a patch would not make smaller. Replace. */
export interface LiveResultFrame { type: 'result'; sub: string; result: QueryResult }
export interface LivePatchFrame { type: 'patch'; sub: string; patch: LiveQueryPatch }
/** The subscription has ended. Without `sub` it answers an unparseable frame. */
export interface LiveErrorFrame { type: 'error'; sub?: string; code: LiveErrorCode; message: string }
/** Heartbeat. Nothing to answer. */
export interface LivePingFrame { type: 'ping' }

export type LiveServerFrame =
  | LivePoFrame | LivePoPatchFrame | LiveGoneFrame
  | LiveResultFrame | LivePatchFrame
  | LiveErrorFrame | LivePingFrame;

/** Close codes with a meaning of their own; any other code is reconnected with backoff. */
export const LIVE_CLOSE_REAUTHENTICATE = 4401;
export const LIVE_CLOSE_TOO_MANY_SOCKETS = 4429;
export const LIVE_CLOSE_MESSAGE_TOO_LARGE = 1009;

// --- what a watcher receives ---------------------------------------------------------------------

/**
 * Why a watcher's subscription ended, beyond the server's own {@link LiveErrorCode}s — the socket-level
 * failures the client surfaces instead of retrying:
 * - `tooManySockets`: closed with 4429; not reconnected until a new watcher asks.
 * - `unauthenticated`: closed with 4401 again within seconds of reconnecting for a 4401.
 * - `messageTooLarge`: closed with 1009, a client bug; not retried blindly.
 */
export type LiveFailureCode = LiveErrorCode | 'tooManySockets' | 'unauthenticated' | 'messageTooLarge';

export interface LiveErrorEvent { type: 'error'; code: LiveFailureCode; message: string }

/** `po`: the object with every patch applied. `gone` and `error` end the stream (it completes after them). */
export type LivePoEvent =
  | { type: 'po'; po: PersistentObject }
  | { type: 'gone' }
  | LiveErrorEvent;

/** `result`: the page with every patch applied. `error` ends the stream (it completes after it). */
export type LiveQueryEvent =
  | { type: 'result'; result: QueryResult }
  | LiveErrorEvent;

// --- applying patches ---------------------------------------------------------------------------

/** Assigns `set` onto a copy of `target`: a `null` value removes the field. */
function assignSet<T extends object>(target: T, set: Record<string, unknown> | undefined): T {
  if (!set) return target;
  const next: Record<string, unknown> = { ...(target as Record<string, unknown>) };
  for (const [key, value] of Object.entries(set)) {
    if (value === null) delete next[key];
    else next[key] = value;
  }
  return next as T;
}

/**
 * Applies a `patch` frame to a held page, exactly as the protocol says. A new id in `upsert` that `order`
 * does not place (the server always sends `order` when membership changes, so this is defensive) is
 * appended rather than dropped.
 */
export function applyLiveQueryPatch(held: QueryResult, patch: LiveQueryPatch): QueryResult {
  const byId = new Map<string, QueryResultItem>();
  for (const item of held.items ?? []) byId.set(item.id, item);
  const removed = new Set(patch.remove ?? []);
  for (const id of removed) byId.delete(id);
  for (const item of patch.upsert ?? []) byId.set(item.id, item);

  let ids: string[];
  if (patch.order) {
    ids = [...patch.order];
  } else {
    ids = (held.items ?? []).map(i => i.id).filter(id => !removed.has(id));
  }
  const placed = new Set(ids);
  for (const item of patch.upsert ?? []) {
    if (!placed.has(item.id)) {
      ids.push(item.id);
      placed.add(item.id);
    }
  }
  const items = ids.map(id => byId.get(id)).filter((i): i is QueryResultItem => !!i);
  return assignSet({ ...held, items }, patch.set);
}

/** Applies a `poPatch` frame to a held object: `set` assigned, changed attributes replaced by `name`. */
export function applyLivePoPatch(held: PersistentObject, patch: LivePoPatch): PersistentObject {
  let attributes = held.attributes;
  if (patch.attributes?.length) {
    const changed = new Map(patch.attributes.map(a => [a.name, a] as const));
    attributes = held.attributes.map(a => changed.get(a.name) ?? a);
    // An attribute the held object lacks would have come as a whole `po`; kept anyway rather than lost.
    const known = new Set(held.attributes.map(a => a.name));
    for (const attribute of patch.attributes) if (!known.has(attribute.name)) attributes = [...attributes, attribute];
  }
  return assignSet({ ...held, attributes }, patch.set);
}
