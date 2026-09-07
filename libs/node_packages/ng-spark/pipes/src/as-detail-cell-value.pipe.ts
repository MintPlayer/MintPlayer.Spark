import { Pipe, PipeTransform } from '@angular/core';
import { AS_DETAIL_BREADCRUMBS_KEY, EntityAttributeDefinition, PersistentObject, QueryResultItem, selfBreadcrumb } from '@mintplayer/ng-spark/models';

/** What a cell can resolve to: the column's own type, or `null` when it has no value. */
export type AsDetailCellValue = string | number | boolean | Date | null;

/**
 * The value of one cell in an `AsDetail` table, **typed by the column's `dataType`** rather than
 * flattened to a string.
 *
 * <p>
 * This used to end in `return String(value)` for everything, and declare `: string`. That is fine
 * for a cell which is only interpolated, and wrong for one whose consumer inspects it —
 * `spark-grid-cell` branches on the column type and binds `[checked]="display() === true"` and
 * `[indeterminate]="display() == null"` for a boolean, so it needs the value and not a rendering
 * of it.
 * </p>
 * <p>
 * The visible symptom was a detail page showing unticked checkboxes for rows whose edit form showed
 * them ticked — the edit form reads `attribute.objects` directly, so it never went through here. All
 * three states of a `bool?` were affected, and one of them silently:
 * </p>
 * <ul>
 *   <li><b>true</b> — `'true' === true` is false, so it rendered <b>unchecked</b>;</li>
 *   <li><b>null</b> — became `''`, and `'' == null` is false, so it rendered unchecked <b>and
 *       determinate</b>, i.e. claiming to be false;</li>
 *   <li><b>false</b> — `'false'` is not `=== true`, so it rendered correctly <b>by accident</b>.</li>
 * </ul>
 * <p>
 * Types that stay strings do so deliberately, not by omission. `color`, `image` and `url` are bound
 * into a style, an `src` and an `href`, so a string is what they need. `Reference` and `AsDetail`
 * resolve to a breadcrumb, which is a label rather than a value.
 * </p>
 * <p>
 * `date` and `datetime` return a real `Date`. They arrive as ISO strings, and the cell used to
 * print that string verbatim — `2026-09-07T13:25:57.3119825Z` in a table column. Returning the
 * parsed value lets `spark-grid-cell` format it in one place for both grids; an unparseable value
 * falls back to its text, because showing what is there beats showing `Invalid Date`.
 * </p>
 */
@Pipe({ name: 'asDetailCellValue', standalone: true, pure: true })
export class AsDetailCellValuePipe implements PipeTransform {
  transform(
    row: Record<string, any>,
    parentAttr: EntityAttributeDefinition,
    col: EntityAttributeDefinition,
    asDetailRefOptions: Record<string, Record<string, QueryResultItem[]>>,
  ): AsDetailCellValue {
    const value = row[col.name];

    // Before the null short-circuit, because for these types "absent" is a value the consumer has
    // to be able to see: `null` is what makes a checkbox indeterminate rather than false, and it is
    // what distinguishes "no number" from the number zero. `''` erases both.
    switch (col.dataType) {
      case 'boolean':
        return typeof value === 'boolean' ? value : null;

      case 'number':
      case 'decimal': {
        if (value == null || value === '') return null;
        const asNumber = typeof value === 'number' ? value : Number(value);
        // A value that is not a number is passed through as text rather than reported as NaN: the
        // cell's job is to show what is there, and "NaN" tells the reader nothing about it.
        return Number.isNaN(asNumber) ? String(value) : asNumber;
      }

      case 'date':
      case 'datetime': {
        if (value == null || value === '') return null;
        if (value instanceof Date) return Number.isNaN(value.getTime()) ? null : value;
        const parsed = new Date(value as string);
        // Same rule as the number branch: an unparseable date shows its own text rather than
        // "Invalid Date", which would hide the value that is actually stored.
        return Number.isNaN(parsed.getTime()) ? String(value) : parsed;
      }
    }

    if (value == null) return '';

    if (col.dataType === 'Reference' && col.query) {
      // Prefer the breadcrumb the server already resolved by id — it is page-independent, so it
      // renders the label even when the referenced document falls outside the reference query's
      // first options page (issue #185).
      const serverBreadcrumb = (row[AS_DETAIL_BREADCRUMBS_KEY] as Record<string, string> | undefined)?.[col.name];
      if (serverBreadcrumb) return serverBreadcrumb;

      // Fallback: resolve against the loaded options page (legacy path).
      const parentOptions = asDetailRefOptions[parentAttr.name];
      if (parentOptions) {
        const options = parentOptions[col.name];
        if (options) {
          const match = options.find(o => o.id === value);
          if (match) return match.breadcrumb || String(value);
        }
      }
    }

    // A nested-AsDetail column's value is an inner dict, so String(value) produced "[object
    // Object]". The flattener kept that object's server-resolved breadcrumb for exactly this.
    if (col.dataType === 'AsDetail' && typeof value === 'object') {
      return selfBreadcrumb(value as Record<string, any>, col.asDetailType) ?? '';
    }

    return String(value);
  }
}
