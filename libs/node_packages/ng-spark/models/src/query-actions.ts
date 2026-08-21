import { CustomActionDefinition } from './custom-action';
import { SparkQuery } from './spark-query';

/**
 * The custom actions a query should offer, from the entity type's full set.
 *
 * Two filters, in order:
 *
 * 1. `showedOn` must include the query side. The accepted values are `"detail"`,
 *    `"query"` and `"both"` — as the server model and the custom-actions guide have
 *    always documented. Both grids previously tested for `"list"`, a value nothing
 *    emits, so an action authored per the documentation rendered nowhere at all.
 * 2. The query's own `actions` allowlist, when it declares one.
 *
 * ⚠️ This narrows what is DISPLAYED. It is NOT an authorization boundary: the grant
 * is, and it is enforced independently in `ExecuteCustomAction` regardless of which
 * query the caller clicked from — a caller can always POST directly. Never present
 * the allowlist as "restricting" an action.
 */
export function filterQueryActions(
  actions: CustomActionDefinition[],
  query: SparkQuery | null | undefined,
): CustomActionDefinition[] {
  const onQuery = actions.filter(a => a.showedOn === 'query' || a.showedOn === 'both');
  const allowed = query?.actions;
  return allowed ? onQuery.filter(a => allowed.includes(a.name)) : onQuery;
}
