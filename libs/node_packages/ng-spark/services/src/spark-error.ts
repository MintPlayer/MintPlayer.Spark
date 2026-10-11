import { HttpErrorResponse } from '@angular/common/http';
import type { TranslatedString } from '@mintplayer/ng-spark/models';

/** What {@link describeSparkError} needs of the language service (`SparkLanguageService` fits). */
export interface SparkErrorLanguage {
  t(key: string, fallback?: string): string;
  resolve(ts: TranslatedString | undefined): string;
}

type ErrorBody = {
  error?: unknown;
  retryAfterSeconds?: unknown;
  result?: { error?: unknown; retryAfterSeconds?: unknown; errors?: { errorMessage?: unknown }[] } | null;
} | string | null | undefined;

/**
 * The seconds a refused call (429) was told to wait: the envelope's `result.retryAfterSeconds` (core's
 * `ClientResult.Throttled`), a bare body's `retryAfterSeconds`, else the `Retry-After` header in seconds. `null` when
 * the refusal named no wait, or the error is not a 429.
 */
export function sparkRetryAfterSeconds(e: unknown): number | null {
  const err = e as HttpErrorResponse | null | undefined;
  if (err?.status !== 429) return null;
  const body = err.error as ErrorBody;
  const fromBody = typeof body === 'object' && body ? (body.result?.retryAfterSeconds ?? body.retryAfterSeconds) : undefined;
  if (typeof fromBody === 'number' && Number.isFinite(fromBody) && fromBody >= 0) return Math.ceil(fromBody);
  const header = err.headers?.get?.('Retry-After');
  if (header && /^\d+$/.test(header.trim())) return Number(header.trim());
  return null;
}

/**
 * A wait in words, rounded the way a person says it: "a few seconds", "1 minute", "12 minutes", "3 hours", "2 days"
 * (keys `spark.wait.*`, `{0}` is the number).
 */
export function formatSparkWait(seconds: number, lang: Pick<SparkErrorLanguage, 't'>): string {
  const s = Math.max(0, Math.ceil(seconds));
  const say = (unit: string, n: number) =>
    lang.t(`spark.wait.${unit}${n === 1 ? '' : 's'}`, n === 1 ? `1 ${unit}` : `{0} ${unit}s`).replace('{0}', String(n));
  if (s < 60) return lang.t('spark.wait.fewSeconds', 'a few seconds');
  if (s < 3600) return say('minute', Math.ceil(s / 60));
  if (s < 2 * 86400) return say('hour', Math.max(1, Math.round(s / 3600)));
  return say('day', Math.round(s / 86400));
}

/**
 * The readable reason a Spark call failed, for an error banner: the first validation message (a `400`'s
 * `result.errors`), else the server's `result.error` (the envelope every action and write answers with), else a bare
 * body's `error`, else `fallback`. A refusal with a wait (429 + `retryAfterSeconds`/`Retry-After`) gets "Try again in
 * 3 hours." appended, so a quota or cool-down says when, not just no.
 */
export function describeSparkError(e: unknown, lang: SparkErrorLanguage, fallback: string): string {
  const err = e as HttpErrorResponse | null | undefined;
  const body = err?.error as ErrorBody;
  let message: string | null = null;
  if (typeof body === 'object' && body) {
    const first = body.result?.errors?.[0]?.errorMessage;
    if (typeof first === 'string' && first) message = first;
    else if (first && typeof first === 'object') message = lang.resolve(first as TranslatedString) || null;
    if (!message && typeof body.result?.error === 'string' && body.result.error) message = body.result.error;
    if (!message && typeof body.error === 'string' && body.error) message = body.error;
  }
  message ??= fallback;

  const wait = sparkRetryAfterSeconds(e);
  if (wait === null) return message;
  const tryAgain = lang.t('spark.tryAgainIn', 'Try again in {0}.').replace('{0}', formatSparkWait(wait, lang));
  return `${message.trim()} ${tryAgain}`;
}
