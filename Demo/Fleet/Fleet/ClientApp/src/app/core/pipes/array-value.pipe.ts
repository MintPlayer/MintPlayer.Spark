import { Pipe, PipeTransform } from '@angular/core';
import { PersistentObject } from '../models';

@Pipe({ name: 'arrayValue', standalone: true, pure: true })
export class ArrayValuePipe implements PipeTransform {
  transform(attrName: string, item: PersistentObject | null): Record<string, any>[] {
    if (!item) return [];
    const attr = item.attributes.find(a => a.name === attrName);
    if (!attr || !Array.isArray(attr.value)) return [];
    return attr.value;
  }
}
