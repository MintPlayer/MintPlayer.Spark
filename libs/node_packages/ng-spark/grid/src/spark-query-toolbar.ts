import { CustomActionDefinition } from '@mintplayer/ng-spark/models';
import type { SparkQueryRowAction } from '@mintplayer/ng-spark/panels';

/**
 * One button of a query's toolbar (#460, M15): the built-in New or Delete, or a custom action.
 *
 * Built by `SparkQueryGridComponent.toolbarActions()` and rendered by every host of the grid — the
 * query card's header and the query-list page's action bar — so both surfaces offer the same
 * actions, enabled by the same rules.
 */
export interface SparkQueryToolbarAction {
  kind: 'new' | 'delete' | 'custom' | 'addon';
  /** For `addon` (row menu only): the add-on's entry (`SPARK_QUERY_ROW_ACTIONS`). */
  addon?: SparkQueryRowAction;
  /** The action's name, unique within the toolbar. */
  name: string;
  /** Its catalogue entry: label, icon, `selectionRule`, `variant`, confirmation. */
  definition: CustomActionDefinition;
  /** `*bsPriorityNavItem` priority: New first, then Delete, then custom actions by offset. */
  priority: number;
}

/**
 * The button classes for an action's `variant`: an allow-list, so a typo cannot inject a class, and
 * the neutral outline default for anything else. Shared by the detail page, the query card and the
 * query-list page.
 */
export function sparkActionClass(definition: CustomActionDefinition, size?: 'sm'): string {
  const variant = definition.variant?.toLowerCase();
  const sizeClass = size ? ` btn-${size}` : '';
  switch (variant) {
    case 'danger':
    case 'warning':
    case 'primary':
    case 'secondary':
    case 'success':
      return `btn btn-${variant}${sizeClass}`;
    default:
      return `btn btn-outline-primary${sizeClass}`;
  }
}
