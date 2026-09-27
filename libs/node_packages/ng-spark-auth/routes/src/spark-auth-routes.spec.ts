import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Routes, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { describe, expect, it } from 'vitest';

import {
  externalProvider,
  facebookProvider,
  githubProvider,
  googleProvider,
  microsoftProvider,
  sparkAuthRoutes,
  withExternalLogin,
  withLocalLogin,
  withPasskeys,
  withRegistration,
} from './spark-auth-routes';
import { SPARK_AUTH_ROUTE_PATHS, SPARK_EXTERNAL_PROVIDERS } from '@mintplayer/ng-spark-auth/models';

/**
 * #302 — which pages `sparkAuthRoutes` mounts.
 *
 * The change these pin is the direction of the default: pages used to mount unless the application
 * opted *out*, so every app shipped a registration form whether or not it wanted one. Now nothing is
 * mounted unless a feature asks for it, and opt-in is enforced by construction rather than by a flag.
 */
describe('sparkAuthRoutes', () => {
  const childPaths = (...features: Parameters<typeof sparkAuthRoutes>) =>
    sparkAuthRoutes(...features)[0].children.map((child: any) => child.path);

  const tokenValue = (features: Parameters<typeof sparkAuthRoutes>, token: unknown) =>
    sparkAuthRoutes(...features)[0].providers.find((p: any) => p.provide === token).useValue;

  it('emits no routes when no feature is passed', () => {
    // A19. The whole point of the redesign: an application that says nothing gets nothing.
    expect(childPaths()).toEqual([]);
  });

  it('mounts the password family for withLocalLogin', () => {
    expect(childPaths(withLocalLogin()))
      .toEqual(['login', 'login/two-factor', 'forgot-password', 'reset-password']);
  });

  it('does not mount registration alongside local login', () => {
    // The two decisions are genuinely separate: accounts provisioned by an administrator still need
    // password sign-in and password reset.
    expect(childPaths(withLocalLogin())).not.toContain('register');
  });

  it('mounts registration only when asked', () => {
    expect(childPaths(withRegistration())).toEqual(['register']);
  });

  it('mounts the sign-in landing page for withExternalLogin', () => {
    expect(childPaths(withExternalLogin())).toEqual(['sign-in']);
  });

  it('composes features in the order they are passed', () => {
    expect(childPaths(withExternalLogin(), withLocalLogin(), withRegistration())).toEqual([
      'sign-in',
      'login',
      'login/two-factor',
      'forgot-password',
      'reset-password',
      'register',
    ]);
  });

  it('honours path overrides per feature', () => {
    expect(childPaths(
      withLocalLogin({ login: 'signin', forgotPassword: 'help/password' }),
      withRegistration('join'),
      withExternalLogin({ signIn: 'welcome' }),
    )).toEqual(['signin', 'login/two-factor', 'help/password', 'reset-password', 'join', 'welcome']);
  });

  it('publishes paths only for the pages it mounted', () => {
    // Partial, deliberately: a template that links to a sibling must be able to ask whether it
    // exists. A [routerLink] bound to undefined navigates to the current route instead of failing.
    const paths = tokenValue([withExternalLogin()], SPARK_AUTH_ROUTE_PATHS);

    expect(paths).toEqual({ signIn: '/sign-in' });
    expect(paths.login).toBeUndefined();
  });

  it('collects declared provider presentations', () => {
    const providers = tokenValue(
      [withExternalLogin(githubProvider(), externalProvider('Okta', { displayName: 'Work account' }))],
      SPARK_EXTERNAL_PROVIDERS,
    );

    expect(providers.map((p: any) => p.scheme)).toEqual(['GitHub', 'Okta']);
    expect(providers[0].iconClass).toBe('bi bi-github');
    expect(providers[1].displayName).toBe('Work account');
  });

  it('accepts an options object among the providers, in any position', () => {
    expect(childPaths(withExternalLogin({ signIn: 'welcome' }, githubProvider()))).toEqual(['welcome']);
    expect(childPaths(withExternalLogin(githubProvider(), { signIn: 'welcome' }))).toEqual(['welcome']);
  });

  it('provides an empty provider list when none are declared', () => {
    // Declaring nothing is valid and useful — the page still renders whatever the server reports.
    expect(tokenValue([withExternalLogin()], SPARK_EXTERNAL_PROVIDERS)).toEqual([]);
  });

  it('mounts the passkey page only when asked, at its default or overridden path', () => {
    expect(childPaths(withPasskeys())).toEqual(['passkeys']);
    expect(childPaths(withPasskeys('account/passkeys'))).toEqual(['account/passkeys']);
    expect(tokenValue([withPasskeys('account/passkeys')], SPARK_AUTH_ROUTE_PATHS))
      .toEqual({ passkeys: '/account/passkeys' });
    // Independent of the password family: none of its pages come along.
    expect(childPaths(withPasskeys())).not.toContain('login');
  });

  it('publishes every mounted page\'s path when features are combined', () => {
    expect(tokenValue(
      [withLocalLogin({ twoFactor: 'mfa' }), withRegistration(), withExternalLogin(), withPasskeys()],
      SPARK_AUTH_ROUTE_PATHS,
    )).toEqual({
      login: '/login',
      twoFactor: '/mfa',
      forgotPassword: '/forgot-password',
      resetPassword: '/reset-password',
      register: '/register',
      signIn: '/sign-in',
      passkeys: '/passkeys',
    });
  });

  it('collects providers from every withExternalLogin call, in order', () => {
    const providers = tokenValue(
      [withExternalLogin(githubProvider()), withLocalLogin(), withExternalLogin(googleProvider())],
      SPARK_EXTERNAL_PROVIDERS,
    );

    expect(providers.map((p: any) => p.scheme)).toEqual(['GitHub', 'Google']);
  });

  it('lazily loads the library\'s own component for each default route', async () => {
    // The loaders are the only reference to each page's chunk, so each must resolve to the page its
    // path names — a swapped import would mount the wrong page with nothing failing.
    const routes = sparkAuthRoutes(withLocalLogin(), withRegistration(), withExternalLogin(), withPasskeys());
    const loaded = await Promise.all(
      routes[0].children.map(async (c: any) => [c.path, (await c.loadComponent()).name]),
    );

    expect(Object.fromEntries(loaded)).toEqual({
      'login': 'SparkLoginComponent',
      'login/two-factor': 'SparkTwoFactorComponent',
      'forgot-password': 'SparkForgotPasswordComponent',
      'reset-password': 'SparkResetPasswordComponent',
      'register': 'SparkRegisterComponent',
      'sign-in': 'SparkSignInComponent',
      'passkeys': 'SparkPasskeysComponent',
    });
  });

  it('loads an explicitly supplied component instead of the library page', async () => {
    const [login] = sparkAuthRoutes(
      withLocalLogin({ login: { path: 'signin', component: CustomPage } }),
    )[0].children;
    const [register] = sparkAuthRoutes(withRegistration({ path: 'join', component: CustomPage }))[0].children;
    const [signIn] = sparkAuthRoutes(withExternalLogin({ signIn: { path: 'welcome', component: CustomPage } }))[0].children;
    const [passkeys] = sparkAuthRoutes(withPasskeys({ path: 'keys', component: CustomPage }))[0].children;

    for (const [route, path] of [[login, 'signin'], [register, 'join'], [signIn, 'welcome'], [passkeys, 'keys']]) {
      expect(route.path).toBe(path);
      expect(await route.loadComponent()).toBe(CustomPage);
    }
  });

  it('keeps the library page when an entry object gives a path but no component', async () => {
    const [register] = sparkAuthRoutes(withRegistration({ path: 'join' } as any))[0].children;

    expect(register.path).toBe('join');
    expect((await register.loadComponent()).name).toBe('SparkRegisterComponent');
  });
});

describe('provider presets', () => {
  it.each([
    [githubProvider, 'GitHub', 'bi bi-github'],
    [googleProvider, 'Google', 'bi bi-google'],
    [facebookProvider, 'Facebook', 'bi bi-facebook'],
    [microsoftProvider, 'Microsoft', 'bi bi-microsoft'],
  ])('%o declares its scheme and icon', (preset, scheme, iconClass) => {
    expect(preset()).toEqual({ scheme, iconClass });
  });

  it('lets presentation override the preset icon and add a label and order', () => {
    expect(googleProvider({ iconClass: 'custom', displayName: 'Work', order: 2 }))
      .toEqual({ scheme: 'Google', iconClass: 'custom', displayName: 'Work', order: 2 });
  });

  it('builds a bare provider from just a scheme', () => {
    expect(externalProvider('Okta')).toEqual({ scheme: 'Okta' });
  });
});

@Component({ standalone: true, template: 'custom' })
class CustomPage {}

@Component({ standalone: true, template: 'fallback' })
class FallbackComponent {}

/**
 * A20 — absence asserted by *navigation* rather than by array shape.
 *
 * An array assertion proves the route object is missing; this proves the URL does not activate a
 * Spark page, which is the property an application actually depends on. The two can come apart if a
 * path is ever mounted from somewhere else.
 */
describe('sparkAuthRoutes navigation', () => {
  async function harnessFor(features: Parameters<typeof sparkAuthRoutes>) {
    const routes: Routes = [
      ...sparkAuthRoutes(...features),
      { path: '**', component: FallbackComponent },
    ];

    TestBed.configureTestingModule({ providers: [provideRouter(routes)] });
    return RouterTestingHarness.create();
  }

  it('does not activate a Spark page for an un-opted path', async () => {
    const harness = await harnessFor([withExternalLogin()]);

    await harness.navigateByUrl('/register');

    expect(harness.routeNativeElement!.textContent).toContain('fallback');
  });

  it('does not activate the password form when only registration was opted into', async () => {
    const harness = await harnessFor([withRegistration()]);

    await harness.navigateByUrl('/login');

    expect(harness.routeNativeElement!.textContent).toContain('fallback');
  });

  it('activates nothing at all when no feature was passed', async () => {
    const harness = await harnessFor([]);

    for (const url of ['/login', '/register', '/sign-in', '/forgot-password', '/reset-password']) {
      await harness.navigateByUrl(url);
      expect(harness.routeNativeElement!.textContent).toContain('fallback');
    }
  });
});
