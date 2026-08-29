import { Pipe, PipeTransform } from '@angular/core';
import {
  LookupReference,
  QueryColumn,
  QueryResultItem,
  resolveTranslation,
  valueFor,
} from '@mintplayer/ng-spark/models';
import { ReferenceChip } from './reference-chips.pipe';

/**
 * The display text of one query-grid cell.
 *
 * The attribute-shaped sibling of this ({@link AttributeValuePipe}) still serves detail and edit
 * pages, which do work in attributes. They are deliberately not one pipe: a query row is a flat
 * projection with no nested objects and no per-row metadata, so the fallbacks that make sense
 * there — reaching into `attr.object`, recomputing a breadcrumb template client-side — are not
 * merely unnecessary here, they are unreachable. Sharing the code would mean carrying branches
 * that can never fire and inviting the next reader to feed one shape into the other.
 */
@Pipe({ name: 'queryCellValue', standalone: true, pure: true })
export class QueryCellValuePipe implements PipeTransform {
  transform(
    column: QueryColumn,
    item: QueryResultItem | null,
    lookupRefOptions: Record<string, LookupReference>,
  ): any {
    const cell = valueFor(item, column.name);
    if (!cell) return '';

    // A resolved reference display wins: the server can resolve breadcrumb templates this side
    // cannot (a template may name a computed property `[IgnoreProperty]` keeps out of the model).
    if (cell.breadcrumb) return cell.breadcrumb;

    if (column.dataType === 'AsDetail') {
      // An array cell carries a child COUNT rather than the children, so the wording — and its
      // pluralisation — is decided here, where the language is. A single-child cell carries the
      // child object (for a renderer to read) and is displayed by the server-resolved breadcrumb
      // above, never by stringifying it here.
      if (!column.isArray) return '';
      const count = typeof cell.value === 'number' ? cell.value : 0;
      return count === 0 ? '' : `${count} item${count !== 1 ? 's' : ''}`;
    }

    if (column.lookupReferenceType && cell.value != null && cell.value !== '') {
      const lookupRef = lookupRefOptions[column.lookupReferenceType];
      const option = lookupRef?.values.find(v => v.key === String(cell.value));
      if (option) return resolveTranslation(option.values) || option.key;
    }

    // Null rather than '' so the cell can tell "false" from "unset" and render an indeterminate
    // checkbox instead of an unchecked one.
    if (column.dataType === 'boolean') return cell.value ?? null;

    return cell.value ?? '';
  }
}

/**
 * Resolves a multi-reference column (`dataType === 'Reference'`, `isArray === true`) to chips.
 *
 * The cell's `value` carries the id array and `breadcrumbs` the server-resolved id → label map,
 * falling back to the id itself when a label is missing — the same display rule as a single
 * reference, applied per id.
 */
@Pipe({ name: 'queryReferenceChips', standalone: true, pure: true })
export class QueryReferenceChipsPipe implements PipeTransform {
  transform(column: QueryColumn, item: QueryResultItem | null): ReferenceChip[] {
    const cell = valueFor(item, column.name);
    if (!cell || !Array.isArray(cell.value)) return [];

    const breadcrumbs = cell.breadcrumbs ?? {};
    return cell.value
      .filter(v => v != null && v !== '')
      .map(v => {
        const id = String(v);
        return { id, label: breadcrumbs[id] || id };
      });
  }
}
