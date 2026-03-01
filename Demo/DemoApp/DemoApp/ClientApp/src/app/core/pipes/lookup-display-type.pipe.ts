import { Pipe, PipeTransform } from '@angular/core';
import { ELookupDisplayType, EntityAttributeDefinition, LookupReference } from '../models';

@Pipe({ name: 'lookupDisplayType', standalone: true, pure: true })
export class LookupDisplayTypePipe implements PipeTransform {
  transform(attr: EntityAttributeDefinition, lookupRefOptions: Record<string, LookupReference>): ELookupDisplayType {
    const lookupRef = attr.lookupReferenceType ? lookupRefOptions[attr.lookupReferenceType] : null;
    return lookupRef?.displayType ?? ELookupDisplayType.Dropdown;
  }
}
