import { Pipe, PipeTransform } from '@angular/core';
import { AS_DETAIL_BREADCRUMBS_KEY, EntityAttributeDefinition, PersistentObject } from '@mintplayer/ng-spark/models';

@Pipe({ name: 'asDetailCellValue', standalone: true, pure: true })
export class AsDetailCellValuePipe implements PipeTransform {
  transform(row: Record<string, any>, parentAttr: EntityAttributeDefinition, col: EntityAttributeDefinition, asDetailRefOptions: Record<string, Record<string, PersistentObject[]>>): string {
    const value = row[col.name];
    if (value == null) return '';

    if (col.dataType === 'Reference' && col.query) {
      // Prefer the breadcrumb the server already resolved by id — it is page-independent, so it
      // renders the label even when the referenced document falls outside the reference query's
      // first options page (issue #185).
      const serverBreadcrumb = (row[AS_DETAIL_BREADCRUMBS_KEY] as Record<string, string> | undefined)?.[col.name];
      if (serverBreadcrumb) return serverBreadcrumb;

      // Fallback: resolve against the loaded options page (legacy path).
      const parentOptions = asDetailRefOptions[parentAttr.name];
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
