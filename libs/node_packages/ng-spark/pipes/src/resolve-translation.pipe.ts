import { Pipe, PipeTransform } from '@angular/core';
import { TranslatedString, resolveTranslation } from '@mintplayer/ng-spark/models';

@Pipe({ name: 'resolveTranslation', standalone: true, pure: false })
export class ResolveTranslationPipe implements PipeTransform {
  transform(value: TranslatedString | null | undefined, fallback?: string): string {
    return resolveTranslation(value) || fallback || '';
  }
}
