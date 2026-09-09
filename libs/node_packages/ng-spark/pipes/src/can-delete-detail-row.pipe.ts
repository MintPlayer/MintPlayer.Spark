import { Pipe, PipeTransform } from '@angular/core';
import { EntityAttributeDefinition, EntityPermissions } from '@mintplayer/ng-spark/models';

@Pipe({ name: 'canDeleteDetailRow', standalone: true, pure: true })
export class CanDeleteDetailRowPipe implements PipeTransform {
  transform(attr: EntityAttributeDefinition, permissions: Record<string, EntityPermissions>): boolean {
    const perms = permissions[attr.name];
    // Fail closed. An absent entry means the child type's permissions were never fetched, not that
    // the user may do this -- and since the save path now enforces `Delete/{RowType}`, defaulting
    // to true renders a button whose save is refused. The fetch is also not guaranteed: it used to
    // go through the entity-type catalogue, which is Query-gated, so a user with `Delete` but not
    // `Query` never got an entry at all.
    return perms ? perms.canDelete : false;
  }
}
