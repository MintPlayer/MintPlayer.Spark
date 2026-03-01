import { Pipe, PipeTransform } from '@angular/core';
import { EntityAttributeDefinition, PersistentObject } from '../models';

@Pipe({ name: 'asDetailCellValue', standalone: true, pure: true })
export class AsDetailCellValuePipe implements PipeTransform {
  transform(
    row: Record<string, any>,
    parentAttr: EntityAttributeDefinition,
    col: EntityAttributeDefinition,
    asDetailReferenceOptions: Record<string, Record<string, PersistentObject[]>>
  ): string {
    const value = row[col.name];
    if (value == null) return '';

    // For Reference columns, resolve breadcrumb from AsDetail reference options
    if (col.dataType === 'Reference' && col.query) {
      const parentOptions = asDetailReferenceOptions[parentAttr.name];
      if (parentOptions) {
        const options = parentOptions[col.name];
        if (options) {
          const match = options.find(o => o.id === value);
          if (match) return match.breadcrumb || match.name || String(value);
        }
      }
    }

    return String(value);
  }
}
