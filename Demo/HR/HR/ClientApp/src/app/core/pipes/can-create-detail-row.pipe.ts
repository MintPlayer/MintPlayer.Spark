import { Pipe, PipeTransform } from '@angular/core';
import { EntityAttributeDefinition, EntityPermissions } from '../models';

@Pipe({ name: 'canCreateDetailRow', standalone: true, pure: true })
export class CanCreateDetailRowPipe implements PipeTransform {
  transform(attr: EntityAttributeDefinition, asDetailPermissions: Record<string, EntityPermissions>): boolean {
    const perms = asDetailPermissions[attr.name];
    return perms ? perms.canCreate : true;
  }
}
