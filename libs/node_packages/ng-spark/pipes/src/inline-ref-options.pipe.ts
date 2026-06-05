import { Pipe, PipeTransform } from '@angular/core';
import { EntityAttributeDefinition, PersistentObject } from '@mintplayer/ng-spark/models';

@Pipe({ name: 'inlineRefOptions', standalone: true, pure: true })
export class InlineRefOptionsPipe implements PipeTransform {
  transform(parentAttr: EntityAttributeDefinition, col: EntityAttributeDefinition, asDetailRefOptions: Record<string, Record<string, PersistentObject[]>>): PersistentObject[] {
    return asDetailRefOptions[parentAttr.name]?.[col.name] || [];
  }
}
