import { TestBed } from '@angular/core/testing';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { provideHttpClient } from '@angular/common/http';
import { describe, expect, it, beforeEach, afterEach, vi } from 'vitest';

import { SparkAuthService } from './spark-auth.service';
import { SPARK_AUTH_CONFIG, defaultSparkAuthConfig } from '@mintplayer/ng-spark-auth/models';

/** Microtask flush — the session re-read is a continuation, so it is not issued until after one. */
const flush = () => new Promise<void>((resolve) => setTimeout(resolve, 0));

/**
 * The account-page half of external logins: list, attach, detach.
 *
 * The detach is the one with teeth. Removing an account's last way in cannot be undone by the
 * person it happens to, so the refusal has to arrive as an answer the caller can act on rather
 * than as a thrown error indistinguishable from the network being down.
 */
describe('SparkAuthService external login management', () => {
  let service: SparkAuthService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: SPARK_AUTH_CONFIG, useValue: defaultSparkAuthConfig },
      ],
    });
    service = TestBed.inject(SparkAuthService);
    http = TestBed.inject(HttpTestingController);

    // Service constructor calls checkAuth().
    http.expectOne('/spark/auth/me').flush(null, { status: 401, statusText: 'Unauthorized' });
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('reads the attached and available providers', async () => {
    const promise = service.externalLogins();

    http.expectOne('/spark/auth/external-logins').flush({
      linked: [{ provider: 'GitHub', providerKey: 'gh-1', displayName: 'GitHub', canUnlink: false }],
      available: [{ provider: 'GitLab', displayName: 'GitLab' }],
    });

    const result = await promise;
    expect(result.linked[0].canUnlink).toBe(false);
    expect(result.available[0].provider).toBe('GitLab');
  });

  it('attaches through the link endpoint, not the sign-in one', () => {
    const open = vi.spyOn(window, 'open').mockReturnValue(
      { closed: false, close: vi.fn() } as unknown as Window);

    service.linkProvider('GitLab', { returnUrl: '/account' });

    const url = open.mock.calls[0][0] as string;
    expect(url).toContain('/spark/auth/external-logins/link');
    expect(url).toContain('provider=GitLab');
    // Without this the callback redirects the popup instead of posting back.
    expect(url).toContain('popup=1');
  });

  it('detaches a provider and re-reads the session', async () => {
    const promise = service.unlinkProvider('GitHub', 'gh-1');

    const request = http.expectOne(
      '/spark/auth/external-logins/unlink?provider=GitHub&providerKey=gh-1');
    expect(request.request.method).toBe('POST');
    request.flush({ unlinked: true });
    await flush();

    http.expectOne('/spark/auth/me').flush(null, { status: 401, statusText: 'Unauthorized' });
    expect(await promise).toEqual({ success: true });
  });

  /**
   * ⚠️ The refusal is an expected answer to a reasonable request, so it resolves rather than
   * throws. A caller that had to distinguish this from a network fault inside a catch block would
   * eventually get it wrong, and the failure mode of getting it wrong is telling someone their
   * unlink worked.
   */
  it('reports the last-credential refusal as an outcome rather than throwing', async () => {
    const promise = service.unlinkProvider('GitHub', 'gh-1');

    http.expectOne('/spark/auth/external-logins/unlink?provider=GitHub&providerKey=gh-1')
      .flush({ error: 'last_credential' }, { status: 400, statusText: 'Bad Request' });

    expect(await promise).toEqual({ success: false, error: 'last_credential' });
  });

  it('falls back to unlink_failed when the server says nothing useful', async () => {
    const promise = service.unlinkProvider('GitHub', 'gh-1');

    http.expectOne('/spark/auth/external-logins/unlink?provider=GitHub&providerKey=gh-1')
      .flush(null, { status: 500, statusText: 'Server Error' });

    expect(await promise).toEqual({ success: false, error: 'unlink_failed' });
  });

  it('does not re-read the session when the detach was refused', async () => {
    const promise = service.unlinkProvider('GitHub', 'gh-1');

    http.expectOne('/spark/auth/external-logins/unlink?provider=GitHub&providerKey=gh-1')
      .flush({ error: 'last_credential' }, { status: 400, statusText: 'Bad Request' });
    await promise;
    await flush();

    // Nothing changed, so there is nothing to re-read — and an unexpected /me here would mean the
    // failure path is running the success path's tail.
    http.verify();
  });
});
