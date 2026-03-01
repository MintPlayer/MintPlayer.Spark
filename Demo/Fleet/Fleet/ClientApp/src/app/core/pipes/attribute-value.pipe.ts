import { Pipe, PipeTransform, inject } from '@angular/core';
import { EntityAttributeDefinition, EntityType, LookupReference, PersistentObject } from '../models';
import { LanguageService } from '../services/language.service';

@Pipe({ name: 'attributeValue', standalone: true, pure: false })
export class AttributeValuePipe implements PipeTransform {
  private readonly lang = inject(LanguageService);

  transform(attrName: string, item: PersistentObject | null, entityType: EntityType | null, lookupReferenceOptions: Record<string, LookupReference>, allEntityTypes: EntityType[]): any {
    if (!item) return '';

    const attr = item.attributes.find(a => a.name === attrName);
    if (!attr) return '';

    if (attr.breadcrumb) return attr.breadcrumb;

    const attrDef = entityType?.attributes.find(a => a.name === attrName);
    if (attrDef?.dataType === 'AsDetail' && attr.value) {
      if (Array.isArray(attr.value)) {
        return `${attr.value.length} item${attr.value.length !== 1 ? 's' : ''}`;
      }
      if (typeof attr.value === 'object') {
        return this.formatAsDetailValue(attrDef, attr.value, allEntityTypes);
      }
    }

    if (attrDef?.lookupReferenceType && attr.value != null && attr.value !== '') {
      const lookupRef = lookupReferenceOptions[attrDef.lookupReferenceType];
      if (lookupRef) {
        const option = lookupRef.values.find(v => v.key === String(attr.value));
        if (option) {
          return this.lang.resolve(option.values) || option.key;
        }
      }
    }

    if (attrDef?.dataType === 'boolean') {
      return attr.value ?? null;
    }

    return attr.value ?? '';
  }

  private formatAsDetailValue(attrDef: EntityAttributeDefinition, value: Record<string, any>, allEntityTypes: EntityType[]): string {
    const asDetailType = allEntityTypes.find(t => t.clrType === attrDef.asDetailType);

    if (asDetailType?.displayFormat) {
      const result = this.resolveDisplayFormat(asDetailType.displayFormat, value);
      if (result && result.trim()) return result;
    }

    if (asDetailType?.displayAttribute && value[asDetailType.displayAttribute]) {
      return value[asDetailType.displayAttribute];
    }

    const displayProps = ['Name', 'Title', 'Street', 'name', 'title'];
    for (const prop of displayProps) {
      if (value[prop]) return value[prop];
    }

    return this.lang.t('notSet');
  }

  private resolveDisplayFormat(format: string, data: Record<string, any>): string {
    return format.replace(/\{(\w+)\}/g, (match, propertyName) => {
      const value = data[propertyName];
      return value != null ? String(value) : '';
    });
  }
}
