import { Pipe, PipeTransform } from '@angular/core';
import { currentLanguage } from '@mintplayer/ng-spark/models';
import { SparkDateTimeMode, SparkDateTimeStyle, formatSparkDateTime } from './spark-date-time-format';

/**
 * `{{ app.firstGrantedAt | sparkDateTime }}`, `{{ at | sparkDateTime:'stored':'medium' }}`: the text of
 * `<spark-date-time>`'s main rendering, for places that need a string (an argument to a translation, a
 * `title`). Impure because it follows the language signal. For a second rendering use the component.
 */
@Pipe({ name: 'sparkDateTime', standalone: true, pure: false })
export class SparkDateTimePipe implements PipeTransform {
  transform(value: unknown, mode: SparkDateTimeMode = 'viewer', format: SparkDateTimeStyle = 'short'): string {
    return formatSparkDateTime(value, mode, format, currentLanguage());
  }
}
