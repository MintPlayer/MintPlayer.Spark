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
      default:
        return 'text';
    }
  }
}
