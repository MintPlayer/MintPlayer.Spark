import { Pipe, PipeTransform, inject } from '@angular/core';
import { EntityAttributeDefinition, EntityType } from '../models';
import { TranslationsService } from '../services/translations.service';

@Pipe({ name: 'asDetailDisplayValue', standalone: true, pure: true })
export class AsDetailDisplayValuePipe implements PipeTransform {
  private readonly translations = inject(TranslationsService);

  transform(attr: EntityAttributeDefinition, formData: Record<string, any>, asDetailTypes: Record<string, EntityType>): string {
    const value = formData[attr.name];
    if (!value) return this.translations.t('notSet');

    const asDetailType = asDetailTypes[attr.name] || null;

    // 1. Try displayFormat (template with {PropertyName} placeholders)
    if (asDetailType?.displayFormat) {
      const result = this.resolveDisplayFormat(asDetailType.displayFormat, value);
      if (result && result.trim()) return result;
    }

    // 2. Try displayAttribute (single property name)
    if (asDetailType?.displayAttribute && value[asDetailType.displayAttribute]) {
      return value[asDetailType.displayAttribute];
    }

    // 3. Fallback to common property names
    const displayProps = ['Name', 'Title', 'Street', 'name', 'title'];
    for (const prop of displayProps) {
      if (value[prop]) return value[prop];
    }

    return this.translations.t('clickToEdit');
  }

  private resolveDisplayFormat(format: string, data: Record<string, any>): string {
    return format.replace(/\{(\w+)\}/g, (match, propertyName) => {
      const value = data[propertyName];
      return value != null ? String(value) : '';
    });
  }
}
