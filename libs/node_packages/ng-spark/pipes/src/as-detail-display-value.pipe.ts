import { Pipe, PipeTransform, inject } from '@angular/core';
import { EntityAttributeDefinition, EntityType, selfBreadcrumb } from '@mintplayer/ng-spark/models';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { applyFieldTemplate } from './apply-field-template';

@Pipe({ name: 'asDetailDisplayValue', standalone: true, pure: true })
export class AsDetailDisplayValuePipe implements PipeTransform {
  private readonly lang = inject(SparkLanguageService);

  transform(attr: EntityAttributeDefinition, formData: Record<string, any>, asDetailTypes: Record<string, EntityType>): string {
    const value = formData[attr.name];
    if (!value) return this.lang.t('notSet');

    const asDetailType = asDetailTypes[attr.name] || null;

    // The server's own resolution wins, because it can render templates this side cannot: a
    // breadcrumb may name a property that `[IgnoreProperty]` deliberately keeps out of the model
    // (HR's `Address.Crumb`), and no amount of client-side substitution will ever find it. Passing
    // the type name filters out the mapper's "template rendered blank" placeholder, which is the
    // bare type name rather than an empty string — see `selfBreadcrumb`.
    const resolved = selfBreadcrumb(value, asDetailType?.name);
    if (resolved) return resolved;

    // Still correct for a template naming real, projected attributes (`"{Street}, {City}"`), which
    // is the common case and the only one reachable on the create path, where there is no server
    // object to have resolved anything yet.
    if (asDetailType?.breadcrumb) {
      const result = applyFieldTemplate(asDetailType.breadcrumb, value);
      if (result && result.trim()) return result;
    }

    return this.lang.t('clickToEdit');
  }
}
