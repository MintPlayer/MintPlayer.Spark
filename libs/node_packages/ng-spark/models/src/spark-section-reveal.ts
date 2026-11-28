/**
 * The DOM event a `#fragment` link's scroller fires on the section it is about to scroll to (#721,
 * `docs/section_fragment_links_PRD.md` Part E). It bubbles, so the object page that drew the section opens the tab that
 * holds it before the scroll.
 */
export const SPARK_REVEAL_EVENT = 'spark-reveal';

/**
 * The attribute on the wrapper of a tab's content: the tab's id. A section's closest wrapper names the tab that holds it.
 */
export const SPARK_TAB_ATTRIBUTE = 'data-spark-tab';

/**
 * The tab holding the section a {@link SPARK_REVEAL_EVENT} came from, or null when it is not on a tab.
 *
 * With `within`, only a wrapper inside that element counts: a component's listener must not open a tab that belongs to
 * a page around it.
 */
export function sparkRevealedTab(event: Event, within?: Element): string | null {
  const target = event.target;
  if (!(target instanceof Element)) return null;
  const wrapper = target.closest(`[${SPARK_TAB_ATTRIBUTE}]`);
  if (!wrapper || (within && !within.contains(wrapper))) return null;
  return wrapper.getAttribute(SPARK_TAB_ATTRIBUTE);
}
