import { Pipe, PipeTransform } from '@angular/core';
import { EntityAttributeDefinition, PersistentObject, QueryResultItem } from '@mintplayer/ng-spark/models';

@Pipe({ name: 'inlineRefOptions', standalone: true, pure: true })
export class InlineRefOptionsPipe implements PipeTransform {
  transform(parentAttr: EntityAttributeDefinition, col: EntityAttributeDefinition, asDetailRefOptions: Record<string, Record<string, QueryResultItem[]>>): QueryResultItem[] {
    return asDetailRefOptions[parentAttr.name]?.[col.name] || [];
  }
}
