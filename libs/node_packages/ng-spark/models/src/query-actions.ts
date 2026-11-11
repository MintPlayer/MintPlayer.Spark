import { CustomActionDefinition } from './custom-action';
import { EntityPermissions } from './entity-permissions';
import { PersistentObject } from './persistent-object';
import { resolveTranslation } from './translated-string';

/**
 * The prompt to show before running `action` on `count` rows, with `{count}` substituted, or null
 * when the action asks for no confirmation (#467, D7).
 */
export function confirmationText(action: CustomActionDefinition | undefined | null, count: number, lang?: string): string | null {
  const text = resolveTranslation(action?.confirmation, lang);
  return text ? text.replace(/\{count\}/g, String(count)) : null;
}

/** The built-in action named `name` (`New`, `Edit`, `Delete`) in `actions`, when the server listed it. */
export function builtInAction(actions: CustomActionDefinition[], name: 'New' | 'Edit' | 'Delete'): CustomActionDefinition | undefined {
  return actions.find(a => !!a.isDefault && a.name.toLowerCase() === name.toLowerCase());
}

/**
 * The Edit and Delete a detail page offers for `item` (#467 D8): listed in the composed catalogue (the caller holds
 * `Edit/T` / `Delete/T`), `showedOn` including the detail side, allowed by the row's `can` when the server attached
 * one (#236 G5; else the type-level `permissions`), and not withheld by `disabledActions` (#460 D13). Nothing on a
 * row opened from the recycle bin: Edit and Delete judge live rows only. Shared by `spark-po-detail` and the custom
 * object pages an app builds on `sparkObjectPage()` (#601 D20).
 */
export function detailBuiltInActions(
  actions: CustomActionDefinition[],
  permissions: EntityPermissions,
  item: PersistentObject,
  deletedView = false,
): { edit: CustomActionDefinition | null; delete: CustomActionDefinition | null } {
  if (deletedView) return { edit: null, delete: null };
  // The override (not an intersection) is safe: the server computes `can` as the intersection of the type-level
  // rights and the row rule (#243), so it never claims more than `permissions` does.
  const can = item.can;
  const withheld = new Set((item.disabledActions ?? []).map(name => name.toLowerCase()));
  const edit = builtInAction(actions, 'Edit');
  const del = builtInAction(actions, 'Delete');
  return {
    edit: edit && isShowedOnDetail(edit.showedOn) && (can ? can.edit : permissions.canEdit)
      && !withheld.has('edit') && !withheld.has('save') ? edit : null,
    delete: del && isShowedOnDetail(del.showedOn) && (can ? can.delete : permissions.canDelete)
      && !withheld.has('delete') ? del : null,
  };
}

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
 * The built-in New, Edit and Delete (`isDefault`, #460 D18, #467 D7) are left out: they are not executed as
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
 * The built-in New, Edit and Delete the server listed for this caller (#460 D18, #467 D7), when their `showedOn`
 * includes the query side. Present only when the caller holds `New/T`, `Edit/T` or `Delete/T`.
 */
export function defaultQueryActions(
  actions: CustomActionDefinition[],
): CustomActionDefinition[] {
  return actions.filter(a => !!a.isDefault && isShowedOnQuery(a.showedOn));
}
