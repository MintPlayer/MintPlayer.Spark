import { Pipe, PipeTransform } from '@angular/core';
import { PersistentObject } from '@mintplayer/ng-spark/models';

export interface ReferenceChip {
  id: string;
  label: string;
}

/**
 * Resolves a multi-reference attribute (`dataType === 'Reference'`, `isArray === true`)
 * to a list of chips. The attribute's `value` carries the id array; `breadcrumbs` (a
 * server-resolved id → label map) provides each chip's display label, falling back to
 * the id when no breadcrumb is present. Mirrors the single-reference `breadcrumb`
 * display, applied per id.
 */
@Pipe({ name: 'referenceChips', standalone: true, pure: true })
export class ReferenceChipsPipe implements PipeTransform {
  transform(attrName: string, item: PersistentObject | null): ReferenceChip[] {
    const attr = item?.attributes.find(a => a.name === attrName);
    if (!attr || !Array.isArray(attr.value)) return [];
    const breadcrumbs = attr.breadcrumbs ?? {};
    return attr.value
      .filter(v => v != null && v !== '')
      .map(v => {
        const id = String(v);
        return { id, label: breadcrumbs[id] || id };
      });
  }
}
