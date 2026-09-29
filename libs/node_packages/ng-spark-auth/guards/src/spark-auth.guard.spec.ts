import { TestBed } from '@angular/core/testing';
import { UrlTree, type ActivatedRouteSnapshot, type RouterStateSnapshot } from '@angular/router';
import { describe, expect, it, beforeEach, vi } from 'vitest';

import { sparkAuthGuard, sparkAuthenticatedGuard } from './spark-auth.guard';
import { SparkAuthService } from '@mintplayer/ng-spark-auth/core';
import { SPARK_AUTH_CONFIG, defaultSparkAuthConfig } from '@mintplayer/ng-spark-auth/models';

describe('sparkAuthGuard', () => {
  let isAuthenticated = false;
  let checkAuth: ReturnType<typeof vi.fn>;

  function configure(config = defaultSparkAuthConfig) {
    TestBed.configureTestingModule({
      providers: [
        { provide: SPARK_AUTH_CONFIG, useValue: config },
        {
          provide: SparkAuthService,
          useValue: { isAuthenticated: () => isAuthenticated, checkAuth },
        },
      ],
    });
  }

  function runGuard(targetUrl: string): Promise<boolean | UrlTree> {
    return Promise.resolve(TestBed.runInInjectionContext(() =>
      sparkAuthGuard(
        {} as ActivatedRouteSnapshot,
        { url: targetUrl } as RouterStateSnapshot,
      ),
    ) as boolean | UrlTree | Promise<boolean | UrlTree>);
  }

  beforeEach(() => {
    isAuthenticated = false;
    checkAuth = vi.fn().mockResolvedValue(null);
  });

  it('returns true when the user is authenticated, without a round trip', async () => {
    isAuthenticated = true;
    configure();

    expect(await runGuard('/anything')).toBe(true);
    expect(checkAuth).not.toHaveBeenCalled();
  });

  // Hard reload: SparkAuthService reads /me asynchronously at start-up, so the signal is still
  // false when the router runs the guard. The guard must wait for the session check instead of
  // sending a signed-in user to the sign-in page.
  it('waits for the session check on a hard reload and lets a signed-in user through', async () => {
    checkAuth = vi.fn().mockResolvedValue({ isAuthenticated: true });
    configure();

    expect(await runGuard('/protected/page')).toBe(true);
    expect(checkAuth).toHaveBeenCalledTimes(1);
  });

  it('returns a UrlTree to the configured login route when unauthenticated', async () => {
    configure();

    const result = await runGuard('/protected/page');

    expect(result).toBeInstanceOf(UrlTree);
    expect((result as UrlTree).toString().startsWith('/login')).toBe(true);
  });

  it('treats a session check that reports an anonymous user as signed out', async () => {
    checkAuth = vi.fn().mockResolvedValue({ isAuthenticated: false });
    configure();

    expect(await runGuard('/protected/page')).toBeInstanceOf(UrlTree);
  });

  it('preserves the requested URL as returnUrl on the redirect', async () => {
    configure();

    const tree = (await runGuard('/protected/page?x=1')) as UrlTree;

    expect(tree.queryParams['returnUrl']).toBe('/protected/page?x=1');
  });

  it('respects a custom loginUrl from the config', async () => {
    configure({ ...defaultSparkAuthConfig, loginUrl: '/auth/signin' });

    const tree = (await runGuard('/somewhere')) as UrlTree;

    expect(tree.toString().startsWith('/auth/signin')).toBe(true);
  });

  it('keeps sparkAuthenticatedGuard as the same guard', () => {
    expect(sparkAuthenticatedGuard).toBe(sparkAuthGuard);
  });
});
