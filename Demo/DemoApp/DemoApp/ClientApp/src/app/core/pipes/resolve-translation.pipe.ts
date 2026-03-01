import { Pipe, PipeTransform } from '@angular/core';
import { TranslatedString, resolveTranslation } from '../models/translated-string';

@Pipe({ name: 'resolveTranslation', standalone: true, pure: true })
export class ResolveTranslationPipe implements PipeTransform {
  transform(value: TranslatedString | undefined, fallback?: string): string {
    return resolveTranslation(value) || fallback || '';
  }
}
