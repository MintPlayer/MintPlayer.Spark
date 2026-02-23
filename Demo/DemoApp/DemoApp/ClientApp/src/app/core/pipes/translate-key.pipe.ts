import { Pipe, PipeTransform, inject } from '@angular/core';
import { TranslationsService } from '../services/translations.service';

@Pipe({ name: 't', pure: false, standalone: true })
export class TranslateKeyPipe implements PipeTransform {
  private readonly translations = inject(TranslationsService);

  transform(key: string): string {
    return this.translations.t(key);
  }
}
