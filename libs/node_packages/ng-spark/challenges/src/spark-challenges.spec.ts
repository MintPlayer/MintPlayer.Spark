import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { SparkConsentService } from '@mintplayer/ng-spark/consent';
import { SPARK_CLIENT_METHODS } from '@mintplayer/ng-spark/client-operations';
import { solveAltcha } from './altcha-solver';
import { SparkAltchaChallenge } from './generated/contracts';
import { SparkChallengeService } from './spark-challenge.service';
import { provideSparkChallenges, SPARK_CHALLENGE_CLIENT_METHOD } from './provide-spark-challenges';

// Commerce #521 M16: the client of MintPlayer.Spark.Challenges.

async function sha256Hex(text: string): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text));
  return Array.from(new Uint8Array(digest), b => b.toString(16).padStart(2, '0')).join('');
}

/** A challenge built the way the server builds it (altcha-lib v1), with a known number. */
async function challengeFor(number: number, maxnumber = 2_000): Promise<SparkAltchaChallenge> {
  const salt = 'a1b2c3d4e5f6a1b2c3d4e5f6?expires=4102444800';
  return { algorithm: 'SHA-256', challenge: await sha256Hex(salt + number), maxnumber, salt, signature: 'sealed-by-server' };
}

describe('challenges: the ALTCHA solver (WebCrypto)', () => {
  it('finds the number and answers the altcha-lib payload', async () => {
    const challenge = await challengeFor(1_234);
    const payload = JSON.parse(atob(await solveAltcha(challenge)));
    expect(payload).toMatchObject({ algorithm: 'SHA-256', challenge: challenge.challenge, number: 1_234, salt: challenge.salt, signature: 'sealed-by-server' });
    expect(typeof payload.took).toBe('number');
  });

  it('gives up beyond maxnumber and can be aborted', async () => {
    await expect(solveAltcha(await challengeFor(500, 100))).rejects.toThrow(/maxnumber/);
    const abort = new AbortController();
    abort.abort();
    await expect(solveAltcha(await challengeFor(1_500), abort.signal)).rejects.toThrow(/Abort/);
  });
});

describe('challenges: the service', () => {
  let consentGranted = false;

  beforeEach(() => {
    consentGranted = false;
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideSparkChallenges(),
        { provide: SparkConsentService, useValue: { has: () => consentGranted } },
      ],
    });
  });

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('fetches an offer for a point and unwraps the envelope; no provider is null', async () => {
    const service = TestBed.inject(SparkChallengeService);
    const http = TestBed.inject(HttpTestingController);

    const offer = service.offer('queue-join', 'fest');
    const request = http.expectOne(r => r.url === '/spark/challenges/offer');
    expect(request.request.params.get('point')).toBe('queue-join');
    expect(request.request.params.get('action')).toBe('fest');
    request.flush({ result: { provider: 'altcha', point: 'queue-join', consentFallback: false, altcha: await challengeFor(3) } });
    expect((await offer)?.provider).toBe('altcha');

    const none = service.offer('hold');
    http.expectOne(r => r.url === '/spark/challenges/offer').flush({}, { status: 404, statusText: 'Not Found' });
    expect(await none).toBeNull();
  });

  it('solves ALTCHA by itself and refuses a hosted provider without consent', async () => {
    const service = TestBed.inject(SparkChallengeService);
    const response = await service.solve({ provider: 'altcha', point: 'hold', consentFallback: false, altcha: await challengeFor(42) });
    expect(response.provider).toBe('altcha');
    expect(JSON.parse(atob(response.payload)).number).toBe(42);

    await expect(service.solve({ provider: 'turnstile', point: 'hold', consentFallback: false, siteKey: 'x', scriptUrl: 'https://challenges.cloudflare.com/turnstile/v0/api.js' }, document.createElement('div')))
      .rejects.toThrow(/consent/);
    expect(document.head.querySelector('script[src*="cloudflare"]')).toBeNull();
  });

  it('registers the spark.challenge client method for the 449 step-up', async () => {
    const methods = TestBed.inject(SPARK_CLIENT_METHODS).find(m => SPARK_CHALLENGE_CLIENT_METHOD in m)!;
    const method = methods[SPARK_CHALLENGE_CLIENT_METHOD];
    expect(method.supported?.()).toBe(true);
    const answer = await TestBed.runInInjectionContext(() =>
      method.invoke({ provider: 'altcha', point: 'hold', consentFallback: false, altcha: { algorithm: 'SHA-256', challenge: '', maxnumber: 0, salt: '', signature: '' } }))
      .catch(() => 'no solution');
    expect(answer).toBe('no solution');
  });
});
