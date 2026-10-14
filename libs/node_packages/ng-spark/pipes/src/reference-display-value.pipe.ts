import { Pipe, PipeTransform, inject } from '@angular/core';
import { EntityAttributeDefinition, PersistentObject, QueryResultItem } from '@mintplayer/ng-spark/models';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { loadedReferenceOf, referenceLabel } from './reference-label';

@Pipe({ name: 'referenceDisplayValue', standalone: true, pure: true })
export class ReferenceDisplayValuePipe implements PipeTransform {
  private readonly lang = inject(SparkLanguageService);

  /**
   * `loadedObject` is the object as read from the server: while the value is still the one it was loaded
   * with, its server-resolved breadcrumb wins over the option rows, which are only the query's first
   * page (see {@link referenceLabel}).
   */
  transform(
    attr: EntityAttributeDefinition,
    formData: Record<string, any>,
    referenceOptions: Record<string, QueryResultItem[]>,
    loadedObject?: PersistentObject | null,
  ): string {
    const selectedId = formData[attr.name];
    if (!selectedId) return this.lang.t('common.notSelected');

    return referenceLabel(selectedId, loadedReferenceOf(loadedObject, attr.name), referenceOptions[attr.name]);
  }
}
