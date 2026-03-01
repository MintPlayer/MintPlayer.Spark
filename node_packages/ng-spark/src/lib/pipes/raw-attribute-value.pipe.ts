import { Pipe, PipeTransform } from '@angular/core';
import { PersistentObject } from '../models';

@Pipe({ name: 'rawAttributeValue', standalone: true, pure: true })
export class RawAttributeValuePipe implements PipeTransform {
  transform(attrName: string, item: PersistentObject | null): any {
    return item?.attributes.find(a => a.name === attrName)?.value;
  }
}
