import { Pipe, type PipeTransform, inject } from '@angular/core';
import { currentLanguage } from '@mintplayer/ng-spark/models';
import { SparkAuthTranslationService } from '@mintplayer/ng-spark/auth/core';

/** Where the pages' texts live: `identityProvider.spa.*` in the identity-provider library's `translations.json`. */
const PREFIX = 'identityProvider.spa.';

function format(template: string, args: unknown[]): string {
  return args.length
    ? template.replace(/\{(\d+)\}/g, (match, index: string) => {
        const arg = args[Number(index)];
        return arg === undefined || arg === null ? match : String(arg);
      })
    : template;
}

/**
 * A text function for component code: `private readonly text = injectIdentityProviderText();` then
 * `this.text('apps.withdrawn')`. The texts come from the server's translations in the app's current
 * language, like every other ng-spark page; `{0}`, `{1}` are replaced by the arguments.
 */
export function injectIdentityProviderText(): (key: string, ...args: unknown[]) => string {
  const translations = inject(SparkAuthTranslationService);
  return (key, ...args) => format(translations.t(PREFIX + key), args);
}

/** `{{ 'apps.title' | idp }}`, `{{ 'apps.by' | idp: publisher }}`. Impure: follows the language and the loaded translations. */
@Pipe({ name: 'idp', pure: false, standalone: true })
export class SparkIdentityProviderTextPipe implements PipeTransform {
  private readonly translations = inject(SparkAuthTranslationService);

  transform(key: string, ...args: unknown[]): string {
    return format(this.translations.t(PREFIX + key), args);
  }
}

/**
 * A server date (ISO string) as a date and time in the app's current language; empty for null.
 * `Intl` rather than Angular's `DatePipe`, which needs locale data registered for every language.
 */
export function identityProviderDate(value: string | null | undefined): string {
  if (!value) return '';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  try {
    return new Intl.DateTimeFormat(currentLanguage(), { dateStyle: 'medium', timeStyle: 'short' }).format(date);
  } catch {
    return date.toLocaleString();
  }
}

/** `{{ app.firstGrantedAt | idpDate }}`. Impure: follows the language signal. */
@Pipe({ name: 'idpDate', pure: false, standalone: true })
export class SparkIdentityProviderDatePipe implements PipeTransform {
  transform(value: string | null | undefined): string {
    return identityProviderDate(value);
  }
}
