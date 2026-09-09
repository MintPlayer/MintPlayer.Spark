import { Pipe, PipeTransform } from '@angular/core';
import { SparkRow, valueFor } from '@mintplayer/ng-spark/models';

/**
 * The text a reference picker's result grid shows in one column.
 *
 * ⚠️ **Reads the row through {@link valueFor}, not through a shape it assumes.** This used to do
 * `item.attributes.find(...)`, which is the `PersistentObject` shape — but a picker's rows are
 * `QueryResultItem`s, which carry `values` (`[{ key, value, breadcrumb }]`) and have no `attributes`
 * at all. Every cell therefore threw `can't access property "find", item.attributes is undefined`,
 * and the picker rendered nothing selectable.
 *
 * There are three row shapes in this codebase and `valueFor` exists precisely so a consumer does not
 * have to know which one it was handed. Using it is also what keeps this working if a picker is ever
 * pointed at an AsDetail sub-table, whose rows are the flat record shape.
 */
@Pipe({ name: 'referenceAttrValue', standalone: true, pure: true })
export class ReferenceAttrValuePipe implements PipeTransform {
  transform(item: SparkRow, attrName: string): any {
    const cell = valueFor(item, attrName);
    if (!cell) return '';
    // Breadcrumb first: a reference cell's value is an id, and the id is not what a person reads.
    if (cell.breadcrumb) return cell.breadcrumb;
    return cell.value ?? '';
  }
}
