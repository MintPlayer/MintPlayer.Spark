import { Pipe, type PipeTransform, inject } from '@angular/core';
import { activeLanguage } from '@mintplayer/ng-spark/models';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';

/** Where the pages' texts live: `apps.spa.*` in MintPlayer.Spark.Apps' `translations.json`. */
const PREFIX = 'apps.spa.';

function format(template: string, args: unknown[]): string {
  return args.length
    ? template.replace(/\{(\d+)\}/g, (match, index: string) => {
        const arg = args[Number(index)];
        return arg === undefined || arg === null ? match : String(arg);
      })
    : template;
}

/** A text function for component code: `this.text('webhooks.deleted')`; `{0}`, `{1}` are replaced by the arguments. */
export function injectAppsText(): (key: string, ...args: unknown[]) => string {
  const language = inject(SparkLanguageService);
  return (key, ...args) => format(language.t(PREFIX + key), args);
}

/** `{{ 'webhooks.title' | sparkApps }}`. Impure: follows the language and the loaded translations. */
@Pipe({ name: 'sparkApps', pure: false, standalone: true })
export class SparkAppsTextPipe implements PipeTransform {
  private readonly language = inject(SparkLanguageService);

  transform(key: string, ...args: unknown[]): string {
    return format(this.language.t(PREFIX + key), args);
  }
}

/** `{{ endpoint.createdUtc | sparkAppsDate }}`: date and time in the app's language; empty for null. */
@Pipe({ name: 'sparkAppsDate', pure: false, standalone: true })
export class SparkAppsDatePipe implements PipeTransform {
  transform(value: string | null | undefined): string {
    if (!value) return '';
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return value;
    try {
      return new Intl.DateTimeFormat(activeLanguage(), { dateStyle: 'medium', timeStyle: 'short' }).format(date);
    } catch {
      return date.toLocaleString();
    }
  }
}
