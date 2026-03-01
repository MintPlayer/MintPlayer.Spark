import { Pipe, PipeTransform } from '@angular/core';
import { EntityType } from '../models';

@Pipe({ name: 'referenceLinkRoute', standalone: true, pure: true })
export class ReferenceLinkRoutePipe implements PipeTransform {
  transform(referenceClrType: string, referenceId: any, allEntityTypes: EntityType[]): string[] | null {
    if (!referenceId || !referenceClrType) return null;
    const targetType = allEntityTypes.find(t => t.clrType === referenceClrType);
    if (!targetType) return null;
    return ['/po', targetType.alias || targetType.id, referenceId];
  }
}
