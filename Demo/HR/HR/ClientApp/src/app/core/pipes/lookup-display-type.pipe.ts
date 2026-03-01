import { Pipe, PipeTransform } from '@angular/core';
import { ELookupDisplayType, EntityAttributeDefinition, LookupReference } from '../models';

@Pipe({ name: 'lookupDisplayType', standalone: true, pure: true })
export class LookupDisplayTypePipe implements PipeTransform {
  transform(attr: EntityAttributeDefinition, lookupReferenceOptions: Record<string, LookupReference>): ELookupDisplayType {
    const lookupRef = attr.lookupReferenceType ? lookupReferenceOptions[attr.lookupReferenceType] : null;
    return lookupRef?.displayType ?? ELookupDisplayType.Dropdown;
  }
}
