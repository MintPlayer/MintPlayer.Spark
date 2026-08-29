import { Pipe, PipeTransform, inject } from '@angular/core';
import { EntityAttributeDefinition, PersistentObject, QueryResultItem } from '@mintplayer/ng-spark/models';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';

@Pipe({ name: 'referenceDisplayValue', standalone: true, pure: true })
export class ReferenceDisplayValuePipe implements PipeTransform {
  private readonly lang = inject(SparkLanguageService);

  transform(attr: EntityAttributeDefinition, formData: Record<string, any>, referenceOptions: Record<string, QueryResultItem[]>): string {
    const selectedId = formData[attr.name];
    if (!selectedId) return this.lang.t('notSelected');

    const options = referenceOptions[attr.name] || [];
    const selected = options.find(o => o.id === selectedId);
    // The server resolves a row's display string into `breadcrumb`; falling back to the id
    // beats showing nothing, and there is no third source now that a row carries no `name`.
    return selected?.breadcrumb || selectedId;
  }
}
