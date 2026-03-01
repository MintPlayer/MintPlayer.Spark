import { Pipe, PipeTransform, inject } from '@angular/core';
import { EntityAttributeDefinition, PersistentObject } from '../models';
import { SparkLanguageService } from '../services/spark-language.service';

@Pipe({ name: 'referenceDisplayValue', standalone: true, pure: true })
export class ReferenceDisplayValuePipe implements PipeTransform {
  private readonly lang = inject(SparkLanguageService);

  transform(attr: EntityAttributeDefinition, formData: Record<string, any>, referenceOptions: Record<string, PersistentObject[]>): string {
    const selectedId = formData[attr.name];
    if (!selectedId) return this.lang.t('notSelected');

    const options = referenceOptions[attr.name] || [];
    const selected = options.find(o => o.id === selectedId);
    return selected?.breadcrumb || selected?.name || selectedId;
  }
}
