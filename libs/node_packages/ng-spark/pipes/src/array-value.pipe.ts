import { Pipe, PipeTransform } from '@angular/core';
import { PersistentObject, nestedPoToDict } from '@mintplayer/ng-spark/models';

/**
 * Resolves an attribute to a list of flat row dicts for the detail-page table.
 * AsDetail array attributes carry their data as nested PersistentObjects in
 * <c>attr.objects</c>, not <c>attr.value</c> — the server stopped putting flat
 * dicts in <c>value</c> when AsDetail moved to its dedicated wire shape. Without
 * this branch, the detail page rendered AsDetail arrays as empty tables even
 * when the edit page (which reads <c>attr.objects</c> directly) showed rows.
 */
@Pipe({ name: 'arrayValue', standalone: true, pure: true })
export class ArrayValuePipe implements PipeTransform {
  transform(attrName: string, item: PersistentObject | null): Record<string, any>[] {
    const attr = item?.attributes.find(a => a.name === attrName);
    if (!attr) return [];
    if (attr.dataType === 'AsDetail' && attr.isArray && Array.isArray(attr.objects)) {
      return attr.objects.map(po => nestedPoToDict(po));
    }
    if (Array.isArray(attr.value)) return attr.value;
    return [];
  }
}
