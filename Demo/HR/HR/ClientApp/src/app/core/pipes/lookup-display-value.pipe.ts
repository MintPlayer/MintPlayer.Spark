import { Pipe, PipeTransform, inject } from '@angular/core';
import { EntityAttributeDefinition, LookupReference } from '../models';
import { LanguageService } from '../services/language.service';

@Pipe({ name: 'lookupDisplayValue', pure: false, standalone: true })
export class LookupDisplayValuePipe implements PipeTransform {
  private readonly lang = inject(LanguageService);

  transform(attr: EntityAttributeDefinition, formData: Record<string, any>, lookupReferenceOptions: Record<string, LookupReference>): string {
    const currentValue = formData[attr.name];
    if (currentValue == null || currentValue === '') return '';

    const lookupRef = attr.lookupReferenceType ? lookupReferenceOptions[attr.lookupReferenceType] : null;
    const options = lookupRef?.values.filter(v => v.isActive) || [];
    const selected = options.find(o => o.key === String(currentValue));
    if (!selected) return String(currentValue);

    return this.lang.resolve(selected.values) || selected.key;
  }
}
