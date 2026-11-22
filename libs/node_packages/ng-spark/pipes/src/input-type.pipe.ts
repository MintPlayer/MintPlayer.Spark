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
      case 'Image':
      case 'BinaryFile':
        // All hold a URL here, so `url` gets the browser's own validation and keyboard. A file attribute only reaches the
        // default input when it is a string (#700): a stored file is drawn by the Files renderer, or by the notice.
        return 'url';
      default:
        return 'text';
    }
  }
}
