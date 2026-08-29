import { Pipe, PipeTransform } from '@angular/core';

@Pipe({ name: 'inputType', standalone: true, pure: true })
export class InputTypePipe implements PipeTransform {
  transform(dataType: string): string {
    switch (dataType) {
      case 'number':
      case 'decimal':
        return 'number';
      case 'boolean':
        return 'checkbox';
      case 'datetime':
        return 'datetime-local';
      case 'date':
        return 'date';
      case 'color':
        return 'color';
      case 'url':
      case 'image':
        // Both hold a URL, so `url` gets the browser's own validation and keyboard. `image` is a
        // link to an image rather than an upload: the value is still a string the author types.
        return 'url';
      default:
        return 'text';
    }
  }
}
