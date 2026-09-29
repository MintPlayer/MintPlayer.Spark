import { SparkSelectionModeSetting } from './selection-mode';

/**
 * One sub-query of a type, with the overrides the parent's type states for it (#460, D17).
 */
export interface SparkSubQuery {
  /** The query's alias or id. */
  query: string;
  /** Overrides the query's own `selectionMode` on this parent's detail page. */
  selectionMode?: SparkSelectionModeSetting;
  /** The row attribute pointing at the parent, when the row type has several (server-side). */
  parentReference?: string;
}

/**
 * An entry of `EntityType.queries` as it arrives on the wire: a bare alias — the shape every model
 * had before #460 M15, and still what the server sends for an entry without overrides — or the
 * object form.
 */
export type SparkSubQueryEntry = string | SparkSubQuery;

/** One entry in its object form, whichever shape it arrived in. */
export function normalizeSubQuery(entry: SparkSubQueryEntry): SparkSubQuery {
  return typeof entry === 'string' ? { query: entry } : entry;
}

/** A type's sub-queries in object form; empty when it declares none. */
export function subQueriesOf(type: { queries?: SparkSubQueryEntry[] } | null | undefined): SparkSubQuery[] {
  return (type?.queries ?? []).map(normalizeSubQuery).filter(entry => !!entry.query);
}
