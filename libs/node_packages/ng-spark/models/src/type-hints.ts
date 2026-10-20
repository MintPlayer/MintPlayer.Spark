/**
 * Presentation hints (`QueryResultItem.typeHints`, `QueryResultItemValue.typeHints`; keys arrive
 * lower-cased) as CSS classes, so an app styles a hinted row or cell without a renderer (commerce #521):
 * every hint gives `spark-hint-<key>`, and `spark-hint-<key>-<value>` when it has a value. Keys and
 * values are reduced to `[a-z0-9-]`, so a hint can never inject markup or another class list.
 *
 * Later sources win on a repeated key (pass the row's hints first, the cell's after).
 */
export function sparkTypeHintClasses(...sources: (Record<string, string> | null | undefined)[]): string {
  const merged = new Map<string, string>();
  for (const hints of sources) {
    if (!hints) continue;
    for (const [key, value] of Object.entries(hints)) {
      const k = hintToken(key);
      if (k) merged.set(k, hintToken(value ?? ''));
    }
  }
  const classes: string[] = [];
  for (const [key, value] of merged) {
    classes.push(`spark-hint-${key}`);
    if (value) classes.push(`spark-hint-${key}-${value}`);
  }
  return classes.join(' ');
}

function hintToken(text: string): string {
  return String(text).toLowerCase().replace(/[^a-z0-9-]+/g, '-').replace(/^-+|-+$/g, '');
}
