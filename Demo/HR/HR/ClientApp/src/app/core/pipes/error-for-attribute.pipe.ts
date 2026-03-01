import { Pipe, PipeTransform } from '@angular/core';
import { ValidationError, resolveTranslation } from '../models';

@Pipe({ name: 'errorForAttribute', standalone: true, pure: true })
export class ErrorForAttributePipe implements PipeTransform {
  transform(attrName: string, validationErrors: ValidationError[]): string | null {
    const error = validationErrors.find(e => e.attributeName === attrName);
    return error ? resolveTranslation(error.errorMessage) : null;
  }
}
