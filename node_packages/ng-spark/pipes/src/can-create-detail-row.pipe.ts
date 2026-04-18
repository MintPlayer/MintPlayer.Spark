import { Pipe, PipeTransform } from '@angular/core';
import { EntityAttributeDefinition, EntityPermissions } from '@mintplayer/ng-spark/models';

@Pipe({ name: 'canCreateDetailRow', standalone: true, pure: true })
export class CanCreateDetailRowPipe implements PipeTransform {
  transform(attr: EntityAttributeDefinition, permissions: Record<string, EntityPermissions>): boolean {
    const perms = permissions[attr.name];
    return perms ? perms.canCreate : true;
  }
}
