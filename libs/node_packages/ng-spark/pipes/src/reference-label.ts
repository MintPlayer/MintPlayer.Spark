import { PersistentObject, QueryResultItem } from '@mintplayer/ng-spark/models';

/**
 * The label of a Reference as it was loaded: the id the object held and the label the server resolved
 * for it (`PersistentObjectAttribute.breadcrumb`, or `breadcrumbs[id]` for a multi-reference).
 */
export interface LoadedReference {
  /** The id, or ids for a multi-reference, the attribute held when the object was loaded. */
  value: string | readonly string[] | null | undefined;
  /** The server-resolved label of a single reference. */
  breadcrumb?: string | null;
  /** The server-resolved label per id of a multi-reference. */
  breadcrumbs?: Record<string, string | null> | null;
}

/** The loaded reference of `attributeName` on `po`, or undefined when the object has no such attribute. */
export function loadedReferenceOf(po: PersistentObject | null | undefined, attributeName: string): LoadedReference | undefined {
  const attr = po?.attributes?.find(a => a.name === attributeName);
  return attr ? { value: attr.value, breadcrumb: attr.breadcrumb, breadcrumbs: attr.breadcrumbs } : undefined;
}

/**
 * The text a Reference shows on the edit form, so it matches the query list and the detail page (default
 * index PRD, D10 / requirement 4). In order:
 *
 * 1. the server's label, while `id` is (one of) the id(s) the object was loaded with. The server resolves
 *    it by id with T's template, the same way the list and the detail page get theirs;
 * 2. the chosen option row's `breadcrumb`, once the user picked another value. A picker only ever
 *    selects from those rows, and they render T's template too;
 * 3. the id itself, which beats showing nothing.
 *
 * The options are only one page of the reference query (50 by default), so resolving from them alone,
 * as the picker used to, showed the stored value as a raw id whenever it fell outside that page.
 */
export function referenceLabel(
  id: string,
  loaded: LoadedReference | null | undefined,
  options: readonly QueryResultItem[] | null | undefined,
): string {
  const server = loadedReferenceLabel(id, loaded);
  if (server) return server;
  const option = options?.find(o => o.id === id)?.breadcrumb;
  if (option) return option;
  return id;
}

/** The server label of `id` when it is (one of) the id(s) the reference was loaded with, else null. */
export function loadedReferenceLabel(id: string, loaded: LoadedReference | null | undefined): string | null {
  if (!loaded) return null;
  if (Array.isArray(loaded.value)) {
    return loaded.value.includes(id) ? nonEmpty(loaded.breadcrumbs?.[id]) : null;
  }
  return loaded.value === id ? nonEmpty(loaded.breadcrumb) : null;
}

function nonEmpty(label: string | null | undefined): string | null {
  return typeof label === 'string' && label.trim() !== '' ? label : null;
}
