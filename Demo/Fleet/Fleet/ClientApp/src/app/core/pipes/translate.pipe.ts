import { Pipe, PipeTransform, inject } from '@angular/core';
import { TranslatedString } from '../models';
import { LanguageService } from '../services/language.service';

@Pipe({ name: 'translate', pure: false, standalone: true })
export class TranslatePipe implements PipeTransform {
  private readonly lang = inject(LanguageService);

  transform(value: TranslatedString | undefined): string {
    return this.lang.resolve(value);
  }
}
