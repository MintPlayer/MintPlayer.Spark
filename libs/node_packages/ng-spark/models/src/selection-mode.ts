import { CustomActionDefinition } from './custom-action';
import { parseSelectionRule } from './selection-rule';

/**
 * Whether a grid lets the user tick rows — what `<bs-datatable>` renders. There is no single
 * selection (#467, D10): a selectable list is a checkbox list, and actions whose rule does not
 * match the count are disabled rather than the selection trimmed.
 */
export type SparkSelectionMode = 'none' | 'multiple';

/**
 * What a query definition (or a sub-query entry) declares (#460 D17): a fixed mode, or `'auto'` —
 * the default, and what an absent field means — to derive it from the actions offered.
 */
export type SparkSelectionModeSetting = 'auto' | SparkSelectionMode;

/** The largest selection the server accepts in one request (`SparkDefaultActions.MaxSelectedItems`). */
export const SPARK_MAX_SELECTED_ITEMS = 200;

/**
 * Whether an action needs ticked rows (#467, R1(4)): it has a rule, and the rule accepts at least
 * one row. `=0` accepts none, so an action that runs only without a selection never makes a list
 * selectable; an action without a rule acts on the query, not on rows.
 */
export function needsSelection(action: CustomActionDefinition): boolean {
  if (!action.selectionRule?.trim()) return false;
  const rule = parseSelectionRule(action.selectionRule);
  for (let count = 1; count <= SPARK_MAX_SELECTED_ITEMS; count++)
    if (rule(count)) return true;
  return false;
}

/**
 * The selection mode a grid renders (#467, R1).
 *
 * A declared `'none'` or `'multiple'` wins outright. `'auto'` (or nothing) derives it from
 * `actions`, which must already be the **effective** list for this result — listed for the caller
 * (the right), shown on queries, not withheld by `disabledActions` and not hidden by the recycle
 * bin. Built-in Edit and Delete count like any other action. `'multiple'` when at least one of them
 * {@link needsSelection}, else `'none'`, so a read-only user sees a list with no checkbox column.
 */
export function selectionModeFor(
  actions: CustomActionDefinition[],
  declared?: SparkSelectionModeSetting | null,
): SparkSelectionMode {
  if (declared && declared !== 'auto') return declared;
  return actions.some(needsSelection) ? 'multiple' : 'none';
}
