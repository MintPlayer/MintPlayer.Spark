import { Pipe, PipeTransform, inject } from '@angular/core';
import { SparkAuthTranslationService } from '../services/spark-auth-translation.service';

@Pipe({ name: 't', pure: false, standalone: true })
export class TranslateKeyPipe implements PipeTransform {
  private readonly translation = inject(SparkAuthTranslationService);

  transform(key: string): string {
    return this.translation.t(key);
  }
}
