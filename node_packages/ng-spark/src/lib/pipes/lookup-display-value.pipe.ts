import { Pipe, PipeTransform } from '@angular/core';
import { EntityAttributeDefinition, LookupReference, resolveTranslation } from '../models';

@Pipe({ name: 'lookupDisplayValue', standalone: true, pure: true })
export class LookupDisplayValuePipe implements PipeTransform {
  transform(attr: EntityAttributeDefinition, formData: Record<string, any>, lookupRefOptions: Record<string, LookupReference>): string {
    const currentValue = formData[attr.name];
    if (currentValue == null || currentValue === '') return '';

    const lookupRef = attr.lookupReferenceType ? lookupRefOptions[attr.lookupReferenceType] : null;
    const options = lookupRef?.values.filter(v => v.isActive) || [];
    const selected = options.find(o => o.key === String(currentValue));
    if (!selected) return String(currentValue);

    return resolveTranslation(selected.values) || selected.key;
  }
}
