import { Pipe, PipeTransform } from '@angular/core';
import { ProgramUnit } from '../models';

@Pipe({ name: 'routerLink', standalone: true, pure: true })
export class RouterLinkPipe implements PipeTransform {
  transform(unit: ProgramUnit): string[] {
    if (unit.type === 'query') {
      return ['/query', unit.alias || unit.queryId!];
    } else if (unit.type === 'persistentObject') {
      return ['/po', unit.alias || unit.persistentObjectId!];
    }
    return ['/'];
  }
}
