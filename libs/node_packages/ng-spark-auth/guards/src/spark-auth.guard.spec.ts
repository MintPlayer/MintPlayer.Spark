import { TestBed } from '@angular/core/testing';
import { UrlTree, type ActivatedRouteSnapshot, type RouterStateSnapshot } from '@angular/router';
import { describe, expect, it, beforeEach } from 'vitest';

import { sparkAuthGuard } from './spark-auth.guard';
import { SparkAuthService } from '@mintplayer/ng-spark-auth/core';
import { SPARK_AUTH_CONFIG, defaultSparkAuthConfig } from '@mintplayer/ng-spark-auth/models';

describe('sparkAuthGuard', () => {
  let isAuthenticated = false;

  function configure(config = defaultSparkAuthConfig) {
    TestBed.configureTestingModule({
      providers: [
        { provide: SPARK_AUTH_CONFIG, useValue: config },
        {
          provide: SparkAuthService,
          useValue: { isAuthenticated: () => isAuthenticated },
        },
      ],
    });
  }

  function runGuard(targetUrl: string): boolean | UrlTree {
    return TestBed.runInInjectionContext(() =>
      sparkAuthGuard(
        {} as ActivatedRouteSnapshot,
        { url: targetUrl } as RouterStateSnapshot,
      ),
    ) as boolean | UrlTree;
  }

  beforeEach(() => {
    isAuthenticated = false;
  });

  it('returns true when the user is authenticated', () => {
    isAuthenticated = true;
    configure();

    expect(runGuard('/anything')).toBe(true);
  });

  it('returns a UrlTree to the configured login route when unauthenticated', () => {
    configure();

    const result = runGuard('/protected/page');

    expect(result).toBeInstanceOf(UrlTree);
    expect((result as UrlTree).toString().startsWith('/login')).toBe(true);
  });

  it('preserves the requested URL as returnUrl on the redirect', () => {
    configure();

    const tree = runGuard('/protected/page?x=1') as UrlTree;

    expect(tree.queryParams['returnUrl']).toBe('/protected/page?x=1');
  });

  it('respects a custom loginUrl from the config', () => {
    configure({ ...defaultSparkAuthConfig, loginUrl: '/auth/signin' });

    const tree = runGuard('/somewhere') as UrlTree;

    expect(tree.toString().startsWith('/auth/signin')).toBe(true);
  });
});
