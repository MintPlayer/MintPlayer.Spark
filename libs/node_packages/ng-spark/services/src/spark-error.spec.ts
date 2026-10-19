import { HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { describe, expect, it } from 'vitest';
import { describeSparkError, formatSparkWait, sparkRetryAfterSeconds } from './spark-error';
import { resolveTranslation, translated, type TranslatedString } from '@mintplayer/ng-spark/models';

const dutch: Record<string, string> = {
  'spark.tryAgainIn': 'Probeer opnieuw over {0}.',
  'spark.wait.fewSeconds': 'enkele seconden',
  'spark.wait.minute': '1 minuut',
  'spark.wait.minutes': '{0} minuten',
  'spark.wait.hour': '1 uur',
  'spark.wait.hours': '{0} uur',
  'spark.wait.day': '1 dag',
  'spark.wait.days': '{0} dagen',
};
const english = { t: (_key: string, fallback?: string) => fallback ?? _key, resolve: (ts?: TranslatedString) => resolveTranslation(ts, 'en') };
const nl = { t: (key: string, fallback?: string) => dutch[key] ?? fallback ?? key, resolve: (ts?: TranslatedString) => resolveTranslation(ts, 'nl', { defaultLanguage: null }) };

function refusal(status: number, body: unknown, headers?: Record<string, string>): HttpErrorResponse {
  return new HttpErrorResponse({ status, error: body, headers: headers ? new HttpHeaders(headers) : undefined, url: '/spark/actions/execute' });
}

describe('describeSparkError', () => {
  it('reads the envelope result.error an action answers with, not the HTTP status line', () => {
    const e = refusal(400, { result: { error: 'Open the site first.' }, operations: [] });
    expect(describeSparkError(e, english, 'Action failed')).toBe('Open the site first.');
  });

  it('turns a 429 with retryAfterSeconds into the reason and when to try again', () => {
    const e = refusal(429, { result: { error: 'example.com was audited recently.', retryAfterSeconds: 3 * 3600 - 20 }, operations: [] });
    expect(describeSparkError(e, english, 'Action failed')).toBe('example.com was audited recently. Try again in 3 hours.');
    expect(describeSparkError(e, nl, 'Mislukt')).toBe('example.com was audited recently. Probeer opnieuw over 3 uur.');
  });

  it('falls back to the Retry-After header, and says nothing about waiting without one', () => {
    expect(describeSparkError(refusal(429, { result: { error: 'Quota.' } }, { 'Retry-After': '90' }), english, 'x')).toBe('Quota. Try again in 2 minutes.');
    expect(describeSparkError(refusal(429, { result: { error: 'Quota.' } }), english, 'x')).toBe('Quota.');
  });

  it('prefers the first validation message, translated, then a bare error, then the fallback', () => {
    expect(describeSparkError(refusal(400, { result: { errors: [{ errorMessage: translated({ en: 'Name is required', nl: 'Naam is verplicht' }) }] } }), nl, 'x')).toBe('Naam is verplicht');
    expect(describeSparkError(refusal(404, { error: 'Not found' }), english, 'x')).toBe('Not found');
    expect(describeSparkError(refusal(500, null), english, 'Action failed')).toBe('Action failed');
    expect(describeSparkError(new Error('boom'), english, 'Action failed')).toBe('Action failed');
  });
});

describe('sparkRetryAfterSeconds', () => {
  it('only answers for a 429', () => {
    expect(sparkRetryAfterSeconds(refusal(400, { result: { retryAfterSeconds: 10 } }))).toBeNull();
    expect(sparkRetryAfterSeconds(refusal(429, { retryAfterSeconds: 10.2 }))).toBe(11);
  });
});

describe('formatSparkWait', () => {
  it.each([
    [5, 'a few seconds'], [60, '1 minute'], [61, '2 minutes'], [3599, '60 minutes'], [3600, '1 hour'],
    [5 * 3600 + 1000, '5 hours'], [47 * 3600, '47 hours'], [3 * 86400, '3 days'],
  ])('%i s reads "%s"', (seconds, text) => {
    expect(formatSparkWait(seconds, english)).toBe(text);
  });
});
