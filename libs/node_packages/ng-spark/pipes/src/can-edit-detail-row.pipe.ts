import { Pipe, PipeTransform } from '@angular/core';
import { EntityAttributeDefinition, EntityPermissions } from '@mintplayer/ng-spark/models';

/**
 * Whether the caller may edit rows of an AsDetail attribute's row type.
 *
 * The third of the `can*DetailRow` family, and the one the server was already answering. `canEdit`
 * has been computed, shipped and fetched per AsDetail attribute since row rights landed — it was
 * simply never read for the row path, so the form offered an edit the save would undo.
 *
 * ⚠️ Apply this **read-only, never hidden**. A row the caller may not edit must stay fully
 * readable; hiding the inputs would hide the data, which is a worse answer than showing it
 * uneditable.
 */
@Pipe({ name: 'canEditDetailRow', standalone: true, pure: true })
export class CanEditDetailRowPipe implements PipeTransform {
  transform(attr: EntityAttributeDefinition, permissions: Record<string, EntityPermissions>): boolean {
    const perms = permissions[attr.name];
    // Fail closed, for the same reason as its two siblings: an absent entry means the row type's
    // permissions were never fetched, not that the user may edit. And the save path enforces
    // `Edit/{RowType}` independently -- it silently restores the stored row and warns -- so
    // defaulting to true offers an edit whose result is discarded.
    return perms ? perms.canEdit : false;
  }
}
