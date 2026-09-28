import { TestBed } from '@angular/core/testing';
import { UrlTree, provideRouter, type ActivatedRouteSnapshot, type RouterStateSnapshot } from '@angular/router';
import { describe, expect, it, vi } from 'vitest';

import { linkedInProvider, sparkAuthRoutes, twitterProvider, withAccount, withPasskeys } from './spark-auth-routes';
import { SPARK_AUTH_CONFIG, SPARK_AUTH_ROUTE_PATHS, defaultSparkAuthConfig } from '@mintplayer/ng-spark-auth/models';
import { SparkAuthService } from '@mintplayer/ng-spark-auth/core';
import { sparkAuthenticatedGuard } from '@mintplayer/ng-spark-auth/guards';

describe('withAccount (#460, D16)', () => {
  const children = (...features: Parameters<typeof sparkAuthRoutes>) => sparkAuthRoutes(...features)[0].children;
  const paths = (...features: Parameters<typeof sparkAuthRoutes>) =>
    sparkAuthRoutes(...features)[0].providers.find((p: any) => p.provide === SPARK_AUTH_ROUTE_PATHS).useValue;

  it('mounts the account pages at their default paths', () => {
    expect(children(withAccount()).map((c: any) => c.path)).toEqual([
      'confirm-email',
      'account/profile',
      'account/password',
      'account/two-factor',
      'account/logins',
      'account/passkeys',
      'account/personal-data',
      'account',
    ]);
    expect(paths(withAccount())).toEqual({
      confirmEmail: '/confirm-email',
      profile: '/account/profile',
      changePassword: '/account/password',
      twoFactorSetup: '/account/two-factor',
      externalLogins: '/account/logins',
      passkeys: '/account/passkeys',
      personalData: '/account/personal-data',
      account: '/account',
    });
  });

  it('guards every signed-in page with the waiting guard, but never confirm-email', () => {
    for (const child of children(withAccount())) {
      if (child.path === 'confirm-email') expect(child.canActivate).toBeUndefined();
      else expect(child.canActivate).toEqual([sparkAuthenticatedGuard]);
    }
    expect(children(withAccount({ canActivate: [] })).every((c: any) => !c.canActivate)).toBe(true);
  });

  it('honours path overrides and exclusions', () => {
    const feature = withAccount({ profile: 'me', confirmEmail: 'verify', exclude: ['externalLogins', 'changePassword'] });
    const mounted = children(feature).map((c: any) => c.path);
    expect(mounted).toContain('me');
    expect(mounted).toContain('verify');
    expect(mounted).not.toContain('account/logins');
    expect(mounted).not.toContain('account/password');
    expect(paths(feature).externalLogins).toBeUndefined();
  });

  it('no path has a parameterised first segment, so it cannot shadow sparkRoutes()', () => {
    for (const child of children(withAccount())) expect(child.path.split('/')[0].startsWith(':')).toBe(false);
  });

  it('lazily loads the library pages', async () => {
    const byPath = Object.fromEntries(children(withAccount()).map((c: any) => [c.path, c]));
    expect((await byPath['confirm-email'].loadComponent()).name).toBe('SparkConfirmEmailComponent');
    expect((await byPath['account/profile'].loadComponent()).name).toBe('SparkAccountProfileComponent');
    expect((await byPath['account/password'].loadComponent()).name).toBe('SparkChangePasswordComponent');
    expect((await byPath['account/two-factor'].loadComponent()).name).toBe('SparkTwoFactorSetupComponent');
    expect((await byPath['account/logins'].loadComponent()).name).toBe('SparkExternalLoginsComponent');
    expect((await byPath['account/passkeys'].loadComponent()).name).toBe('SparkPasskeysComponent');
    expect((await byPath['account/personal-data'].loadComponent()).name).toBe('SparkPersonalDataComponent');
    expect((await byPath['account'].loadComponent()).name).toBe('SparkAccountOverviewComponent');
  });

  it('combines with withPasskeys (the later feature owns the passkeys path)', () => {
    expect(paths(withPasskeys(), withAccount()).passkeys).toBe('/account/passkeys');
  });
});

describe('twitterProvider / linkedInProvider', () => {
  it('match the server presets\' schemes', () => {
    expect(twitterProvider()).toEqual({ scheme: 'Twitter', displayName: 'X', iconClass: 'bi bi-twitter-x' });
    expect(linkedInProvider()).toEqual({ scheme: 'LinkedIn', iconClass: 'bi bi-linkedin' });
    expect(twitterProvider({ displayName: 'Twitter' }).displayName).toBe('Twitter');
  });
});

describe('sparkAuthenticatedGuard', () => {
  function run(auth: Partial<SparkAuthService>) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: SPARK_AUTH_CONFIG, useValue: defaultSparkAuthConfig },
        { provide: SparkAuthService, useValue: auth },
      ],
    });
    return TestBed.runInInjectionContext(() =>
      sparkAuthenticatedGuard({} as ActivatedRouteSnapshot, { url: '/account/profile' } as RouterStateSnapshot)) as Promise<boolean | UrlTree>;
  }

  it('passes a known session without a round trip', async () => {
    const checkAuth = vi.fn();
    expect(await run({ isAuthenticated: (() => true) as any, checkAuth })).toBe(true);
    expect(checkAuth).not.toHaveBeenCalled();
  });

  it('waits for the session check on a hard reload', async () => {
    const result = await run({ isAuthenticated: (() => false) as any, checkAuth: vi.fn().mockResolvedValue({ isAuthenticated: true }) });
    expect(result).toBe(true);
  });

  it('sends an anonymous visitor to sign in with a return url', async () => {
    const result = await run({ isAuthenticated: (() => false) as any, checkAuth: vi.fn().mockResolvedValue(null) });
    expect(result instanceof UrlTree).toBe(true);
    expect(result.toString()).toContain('returnUrl=%2Faccount%2Fprofile');
  });
});
