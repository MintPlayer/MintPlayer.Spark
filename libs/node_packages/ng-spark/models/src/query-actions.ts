import { CustomActionDefinition } from './custom-action';

/** Whether `showedOn` offers the action on a query: `"query"` or `"both"`, in any casing. */
export function isShowedOnQuery(showedOn: string | undefined | null): boolean {
  const value = (showedOn ?? '').trim().toLowerCase();
  return value === 'query' || value === 'both';
}

/** Whether `showedOn` offers the action on a detail page: `"detail"` or `"both"`, in any casing. */
export function isShowedOnDetail(showedOn: string | undefined | null): boolean {
  const value = (showedOn ?? '').trim().toLowerCase();
  return value === 'detail' || value === 'both';
}

/**
 * The custom actions a query should offer, from the entity type's full set.
 *
 * `showedOn` must include the query side. The accepted values are `"detail"`, `"query"`
 * and `"both"` — as the server model and the custom-actions guide have always
 * documented — compared case-insensitively (#460 M15: `"Both"` used to render nowhere).
 *
 * The built-in New and Delete (`isDefault`, #460 D18) are left out: they are not executed as
 * custom actions, and the grid offers them itself (see {@link defaultQueryActions}).
 *
 * ⚠️ This narrows what is DISPLAYED. It is NOT an authorization boundary: the grant
 * is, and it is enforced independently in `ExecuteCustomAction` regardless of which
 * query the caller clicked from — a caller can always POST directly.
 */
export function filterQueryActions(
  actions: CustomActionDefinition[],
): CustomActionDefinition[] {
  return actions.filter(a => !a.isDefault && isShowedOnQuery(a.showedOn));
}

/** The custom actions a detail page should offer — the detail-side counterpart of {@link filterQueryActions}. */
export function filterDetailActions(
  actions: CustomActionDefinition[],
): CustomActionDefinition[] {
  return actions.filter(a => !a.isDefault && isShowedOnDetail(a.showedOn));
}

/**
 * The built-in New and Delete the server listed for this caller (#460, D18), when their `showedOn`
 * includes the query side. Present only when the caller holds `New/T` or `Delete/T`.
 */
export function defaultQueryActions(
  actions: CustomActionDefinition[],
): CustomActionDefinition[] {
  return actions.filter(a => !!a.isDefault && isShowedOnQuery(a.showedOn));
}
