import { Pipe, PipeTransform } from '@angular/core';
import { EntityAttributeDefinition, EntityType, LookupReference, PersistentObject, nestedPoToDict, resolveTranslation } from '@mintplayer/ng-spark/models';

@Pipe({ name: 'attributeValue', standalone: true, pure: true })
export class AttributeValuePipe implements PipeTransform {
  transform(attrName: string, item: PersistentObject | null, entityType: EntityType | null, lookupRefOptions: Record<string, LookupReference>, allEntityTypes: EntityType[]): any {
    if (!item) return '';
    const attr = item.attributes.find(a => a.name === attrName);
    if (!attr) return '';

    if (attr.breadcrumb) return attr.breadcrumb;

    const attrDef = entityType?.attributes.find(a => a.name === attrName);
    if (attrDef?.dataType === 'AsDetail') {
      // Server emits nested PO(s) in attr.objects (array) / attr.object (single) — attr.value is null.
      if (attr.isArray) {
        const count = attr.objects?.length ?? 0;
        if (count === 0) return '';
        return `${count} item${count !== 1 ? 's' : ''}`;
      }
      if (attr.object) {
        return this.formatAsDetailValue(attrDef, nestedPoToDict(attr.object), allEntityTypes);
      }
    }

    if (attrDef?.lookupReferenceType && attr.value != null && attr.value !== '') {
      const lookupRef = lookupRefOptions[attrDef.lookupReferenceType];
      if (lookupRef) {
        const option = lookupRef.values.find(v => v.key === String(attr.value));
        if (option) {
          return resolveTranslation(option.values) || option.key;
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

    return '(object)';
  }

  private resolveDisplayFormat(format: string, data: Record<string, any>): string {
    return format.replace(/\{(\w+)\}/g, (match, propertyName) => {
      const value = data[propertyName];
      return value != null ? String(value) : '';
    });
  }
}
