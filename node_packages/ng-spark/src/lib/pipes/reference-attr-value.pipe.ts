import { Pipe, PipeTransform } from '@angular/core';
import { PersistentObject } from '../models';
import { resolveTranslation, type TranslatedString } from '../models/translated-string';

@Pipe({ name: 'referenceAttrValue', standalone: true, pure: true })
export class ReferenceAttrValuePipe implements PipeTransform {
  transform(item: PersistentObject, attrName: string): any {
    const attr = item.attributes.find(a => a.name === attrName);
    if (!attr) return '';
    if (attr.breadcrumb) return attr.breadcrumb;
    const value = attr.value;
    if (value != null && typeof value === 'object' && !Array.isArray(value)) {
      return resolveTranslation(value as TranslatedString);
    }
    return value ?? '';
  }
}
