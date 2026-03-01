import { Pipe, PipeTransform } from '@angular/core';
import { TranslatedString, resolveTranslation } from '../models/translated-string';

@Pipe({ name: 'resolveTranslation', standalone: true, pure: true })
export class ResolveTranslationPipe implements PipeTransform {
  transform(value: TranslatedString | undefined, lang?: string): string {
    return resolveTranslation(value, lang);
  }
}
