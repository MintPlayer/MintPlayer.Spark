import { EntityType } from './entity-type';
import { resolveTranslation } from './translated-string';

function toStringValue(value: any): string {
  if (!value) return '';
  if (typeof value === 'object') return resolveTranslation(value);
  return String(value);
}

export function resolveDisplayName(entityType: EntityType | null, formData: Record<string, any>, fallback = 'New Item'): string {
  if (entityType?.displayFormat) {
    return entityType.displayFormat.replace(/\{(\w+)\}/g, (_, key) => toStringValue(formData[key]) || '');
  }

  if (entityType?.displayAttribute) {
    const val = toStringValue(formData[entityType.displayAttribute]);
    if (val) return val;
  }

  return toStringValue(formData['Name']) || toStringValue(formData['FullName']) || toStringValue(formData['Title']) || fallback;
}
