import { CustomActionDefinition } from './custom-action';
import { parseSelectionRule } from './selection-rule';

/** How many rows a grid lets the user select — what `<bs-datatable>` renders. */
export type SparkSelectionMode = 'none' | 'single' | 'multiple';

/**
 * What a query definition (or a sub-query entry) declares (#460, D17): a fixed mode, or `'auto'` —
 * the default, and what an absent field means — to derive it from the actions offered.
 */
export type SparkSelectionModeSetting = 'auto' | SparkSelectionMode;

/**
 * The selection mode a grid renders.
 *
 * A declared `'none'`, `'single'` or `'multiple'` wins outright. `'auto'` (or nothing) derives it,
 * so a grid gains a checkbox column exactly when an action needs one and is otherwise
 * pixel-identical to a grid with no selection at all — as Vidyano's query grid does. The offered
 * actions include the default Delete (`>0`) when the caller may delete, so such a grid is
 * multi-select.
 *
 * Derived: `'single'` when every gated action is satisfied by one row and refused by two; anything
 * else that cares about the count gets `'multiple'`.
 */
export function selectionModeFor(
  actions: CustomActionDefinition[],
  declared?: SparkSelectionModeSetting | null,
): SparkSelectionMode {
  if (declared && declared !== 'auto') return declared;

  // An action with no rule is not selection-gated: it acts on the query, not on rows.
  const gated = actions.filter(a => !!a.selectionRule?.trim());
  if (gated.length === 0) return 'none';

  const everyRuleWantsExactlyOne = gated.every(a => {
    const rule = parseSelectionRule(a.selectionRule);
    return rule(1) && !rule(2);
  });

  return everyRuleWantsExactlyOne ? 'single' : 'multiple';
}
