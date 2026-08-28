import { Pipe, PipeTransform } from '@angular/core';
import { ProgramUnit } from '@mintplayer/ng-spark/models';

/**
 * Maps a program unit to the router commands that open it. Type comparisons are exact: the
 * server's loader canonicalizes `type` casing, so tolerating variants here would only mask a
 * server that stopped doing so.
 *
 * Returns null for a `url` unit — an external address is an `<a href>`, not a router link; the
 * component rendering the menu owns that branch.
 */
@Pipe({ name: 'routerLink', standalone: true, pure: true })
export class RouterLinkPipe implements PipeTransform {
  transform(unit: ProgramUnit): string[] | null {
    if (unit.type === 'query') {
      return ['/query', unit.alias || unit.queryId!];
    } else if (unit.type === 'persistentObject') {
      const type = unit.alias || unit.persistentObjectId!;
      return unit.objectId ? ['/po', type, unit.objectId] : ['/po', type];
    } else if (unit.type === 'url') {
      return null;
    }
    return ['/'];
  }
}
