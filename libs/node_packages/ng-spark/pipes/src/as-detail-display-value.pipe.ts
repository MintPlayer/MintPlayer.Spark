import { Pipe, PipeTransform, inject } from '@angular/core';
import { EntityAttributeDefinition, EntityType } from '@mintplayer/ng-spark/models';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';

@Pipe({ name: 'asDetailDisplayValue', standalone: true, pure: true })
export class AsDetailDisplayValuePipe implements PipeTransform {
  private readonly lang = inject(SparkLanguageService);

  transform(attr: EntityAttributeDefinition, formData: Record<string, any>, asDetailTypes: Record<string, EntityType>): string {
    const value = formData[attr.name];
    if (!value) return this.lang.t('notSet');

    const asDetailType = asDetailTypes[attr.name] || null;

    // Resolve the breadcrumb template against the nested object's own fields.
    // (Phase 1: flat substitution mirrors the server's transitional behavior; Phase 5
    // replaces this with a server-emitted breadcrumb on the nested object.)
    if (asDetailType?.breadcrumb) {
      const result = this.resolveDisplayFormat(asDetailType.breadcrumb, value);
      if (result && result.trim()) return result;
    }

    return this.lang.t('clickToEdit');
  }

  private resolveDisplayFormat(format: string, data: Record<string, any>): string {
    return format.replace(/\{(\w+)\}/g, (match, propertyName) => {
      const value = data[propertyName];
      return value != null ? String(value) : '';
    });
  }
}
