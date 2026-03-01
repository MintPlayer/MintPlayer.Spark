import { Pipe, PipeTransform } from '@angular/core';
import { EntityAttributeDefinition, LookupReference, LookupReferenceValue } from '../models';

@Pipe({ name: 'lookupOptions', standalone: true, pure: true })
export class LookupOptionsPipe implements PipeTransform {
  transform(attr: EntityAttributeDefinition, lookupRefOptions: Record<string, LookupReference>): LookupReferenceValue[] {
    const lookupRef = attr.lookupReferenceType ? lookupRefOptions[attr.lookupReferenceType] : null;
    return lookupRef?.values.filter(v => v.isActive) || [];
  }
}
