import { SparkAccountResult } from '@mintplayer/ng-spark-auth/models';

/**
 * The messages to show for a failed account call: the server's validation messages (already written
 * for the user by Identity or a profile contributor), else a translation key for its error code or
 * status. Keys start with `auth.`; the page runs everything through the `t` pipe, which returns
 * anything that is not a key unchanged.
 */
export function accountMessages(result: SparkAccountResult<unknown>): string[] {
  if (result.success) return [];
  if (result.errors) {
    const messages = Object.values(result.errors).flat().filter(m => !!m);
    if (messages.length) return messages;
  }
  if (result.error) return [`auth.accountError.${result.error}`];
  if (result.status === 404) return ['auth.accountUnavailable'];
  return ['auth.accountFailed'];
}

/** The messages for one field of a validation problem, matched case-insensitively. */
export function fieldMessages(result: SparkAccountResult<unknown> | null, field: string): string[] {
  if (!result?.errors) return [];
  const key = Object.keys(result.errors).find(k => k.toLowerCase() === field.toLowerCase());
  return key ? result.errors[key] : [];
}
