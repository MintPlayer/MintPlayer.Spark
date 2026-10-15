import { Pipe, PipeTransform } from '@angular/core';
import { EntityType } from '@mintplayer/ng-spark/models';

@Pipe({ name: 'referenceLinkRoute', standalone: true, pure: true })
export class ReferenceLinkRoutePipe implements PipeTransform {
  transform(referenceClrType: string, referenceId: any, allEntityTypes: EntityType[]): string[] | null {
    if (!referenceId || !referenceClrType) return null;
    const targetType = allEntityTypes.find(t => t.clrType === referenceClrType);
    if (!targetType) return null;

    // Presence in the catalogue means the caller may LIST this type; opening one is a different
    // right. Without this a type granted Query but not Read rendered a clickable reference whose
    // click refused on arrival — while the grid's own first column, three components away, gated on
    // canRead and got it right. Same question, two answers.
    if (targetType.canRead === false) return null;

    // A value object has no document of its own — it lives embedded in its owner and is listed by a
    // custom query that reads the parent — so /po/<type>/<id> has nothing to load and 404s. Read is
    // still granted on such a type (withholding it hides the referencing attribute altogether), so
    // canRead cannot answer this one; the server-derived flag does.
    if (targetType.isValueObject) return null;

    return ['/po', targetType.alias || targetType.id, referenceId];
  }
}
