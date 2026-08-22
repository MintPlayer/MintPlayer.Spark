import { CustomActionDefinition } from './custom-action';
import { parseSelectionRule } from './selection-rule';

export type SparkSelectionMode = 'none' | 'single' | 'multiple';

/**
 * The selection mode a grid needs in order to satisfy the actions offered on it.
 *
 * Derived rather than configured, so a grid gains a checkbox column exactly when an
 * action needs one and is otherwise pixel-identical to a grid with no selection at all.
 * Vidyano's query grid does the same thing — it renders the checkbox column only if some
 * action is selection-gated.
 *
 * `'single'` when every gated action is satisfied by one row and refused by two; anything
 * else that cares about the count gets `'multiple'`.
 */
export function selectionModeFor(actions: CustomActionDefinition[]): SparkSelectionMode {
  // An action with no rule is not selection-gated: it acts on the query, not on rows.
  const gated = actions.filter(a => !!a.selectionRule?.trim());
  if (gated.length === 0) return 'none';

  const everyRuleWantsExactlyOne = gated.every(a => {
    const rule = parseSelectionRule(a.selectionRule);
    return rule(1) && !rule(2);
  });

  return everyRuleWantsExactlyOne ? 'single' : 'multiple';
}
