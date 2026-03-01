import { Pipe, PipeTransform, inject } from '@angular/core';
import { EntityAttributeDefinition, EntityType, LookupReference, PersistentObject } from '../models';
import { LanguageService } from '../services/language.service';

@Pipe({ name: 'attributeValue', pure: false, standalone: true })
export class AttributeValuePipe implements PipeTransform {
  private readonly lang = inject(LanguageService);

  transform(
    attrName: string,
    item: PersistentObject | null,
    entityType: EntityType | null,
    lookupReferenceOptions: Record<string, LookupReference>,
    allEntityTypes: EntityType[]
  ): any {
    const attr = item?.attributes.find(a => a.name === attrName);
    if (!attr) return '';

    // For Reference attributes, breadcrumb is resolved on backend
    if (attr.breadcrumb) return attr.breadcrumb;

    // For AsDetail attributes, format using displayFormat
    const attrDef = entityType?.attributes.find(a => a.name === attrName);
    if (attrDef?.dataType === 'AsDetail' && attr.value) {
      if (Array.isArray(attr.value)) {
        return `${attr.value.length} item${attr.value.length !== 1 ? 's' : ''}`;
      }
      if (typeof attr.value === 'object') {
        return this.formatAsDetailValue(attrDef, attr.value, allEntityTypes);
      }
    }

    // For LookupReference attributes, resolve to translated display name
    if (attrDef?.lookupReferenceType && attr.value != null && attr.value !== '') {
      const lookupRef = lookupReferenceOptions[attrDef.lookupReferenceType];
      if (lookupRef) {
        const option = lookupRef.values.find(v => v.key === String(attr.value));
        if (option) {
          return this.lang.resolve(option.values) || option.key;
        }
      }
    }

    // For boolean attributes, preserve null for indeterminate state
    if (attrDef?.dataType === 'boolean') {
      return attr.value ?? null;
    }

    return attr.value ?? '';
  }

  private formatAsDetailValue(attrDef: EntityAttributeDefinition, value: Record<string, any>, allEntityTypes: EntityType[]): string {
    const asDetailType = allEntityTypes.find(t => t.clrType === attrDef.asDetailType);

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

    return '-';
  }

  private resolveDisplayFormat(format: string, data: Record<string, any>): string {
    return format.replace(/\{(\w+)\}/g, (match, propertyName) => {
      const value = data[propertyName];
      return value != null ? String(value) : '';
    });
  }
}
