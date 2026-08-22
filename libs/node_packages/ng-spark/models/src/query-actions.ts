import { CustomActionDefinition } from './custom-action';

/**
 * The custom actions a query should offer, from the entity type's full set.
 *
 * `showedOn` must include the query side. The accepted values are `"detail"`, `"query"`
 * and `"both"` — as the server model and the custom-actions guide have always
 * documented. Both grids previously tested for `"list"`, a value nothing emits, so an
 * action authored per the documentation rendered nowhere at all.
 *
 * ⚠️ This narrows what is DISPLAYED. It is NOT an authorization boundary: the grant
 * is, and it is enforced independently in `ExecuteCustomAction` regardless of which
 * query the caller clicked from — a caller can always POST directly.
 */
export function filterQueryActions(
  actions: CustomActionDefinition[],
): CustomActionDefinition[] {
  return actions.filter(a => a.showedOn === 'query' || a.showedOn === 'both');
}
