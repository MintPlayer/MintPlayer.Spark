import { Pipe, PipeTransform } from '@angular/core';
import { PersistentObject } from '../models';

@Pipe({ name: 'referenceAttrValue', standalone: true, pure: true })
export class ReferenceAttrValuePipe implements PipeTransform {
  transform(item: PersistentObject, attrName: string): any {
    const attr = item.attributes.find(a => a.name === attrName);
    if (!attr) return '';
    if (attr.breadcrumb) return attr.breadcrumb;
    return attr.value ?? '';
  }
}
