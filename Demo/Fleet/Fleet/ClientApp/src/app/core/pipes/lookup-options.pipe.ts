import { Pipe, PipeTransform } from '@angular/core';
import { EntityAttributeDefinition, LookupReference, LookupReferenceValue } from '../models';

@Pipe({ name: 'lookupOptions', standalone: true, pure: true })
export class LookupOptionsPipe implements PipeTransform {
  transform(attr: EntityAttributeDefinition, lookupReferenceOptions: Record<string, LookupReference>): LookupReferenceValue[] {
    const lookupRef = attr.lookupReferenceType ? lookupReferenceOptions[attr.lookupReferenceType] : null;
    return lookupRef?.values.filter(v => v.isActive) || [];
  }
}
