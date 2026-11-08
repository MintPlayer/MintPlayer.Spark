import { Pipe, PipeTransform, inject } from '@angular/core';
import { Router, UrlTree } from '@angular/router';
import { EntityType, sparkDetailLink } from '@mintplayer/ng-spark/models';

@Pipe({ name: 'referenceLinkRoute', standalone: true, pure: true })
export class ReferenceLinkRoutePipe implements PipeTransform {
  private readonly router = inject(Router);

  transform(referenceClrType: string, referenceId: any, allEntityTypes: EntityType[]): UrlTree | unknown[] | null {
    if (!referenceId || !referenceClrType) return null;
    const targetType = allEntityTypes.find(t => t.clrType === referenceClrType);
    if (!targetType) return null;

    // Presence in the catalogue means the caller may LIST this type; opening one is a different
    // right. Without this a type granted Query but not Read rendered a clickable reference whose
    // click refused on arrival — while the grid's own first column, three components away, gated on
    // canRead and got it right. Same question, two answers.
    if (targetType.canRead === false) return null;

    // A type with its own route template (#602) opens at that URL; the page adds the slug itself. Parsed, because
    // `routerLink` given the path as a string would encode its already-encoded id a second time.
    if (targetType.route) return this.router.parseUrl(sparkDetailLink(targetType, String(referenceId)));
    return ['/po', targetType.alias || targetType.id, referenceId];
  }
}
