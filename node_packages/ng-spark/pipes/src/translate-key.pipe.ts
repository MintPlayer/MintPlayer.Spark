import { Pipe, PipeTransform, inject } from '@angular/core';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';

@Pipe({ name: 't', pure: false, standalone: true })
export class TranslateKeyPipe implements PipeTransform {
  private readonly lang = inject(SparkLanguageService);

  transform(key: string): string {
    return this.lang.t(key);
  }
}
