import { describe, expect, it } from 'vitest';
import { sparkAuthRoutes } from './spark-auth-routes';

describe('sparkAuthRoutes', () => {
  it('produces a single parent route with five children by default', () => {
    const routes = sparkAuthRoutes();

    expect(routes).toHaveLength(1);
    expect(routes[0].children).toHaveLength(5);
  });

  it('uses default paths when no config is provided', () => {
    const [{ children }] = sparkAuthRoutes();

    expect(children.map((c: any) => c.path)).toEqual([
      'login',
      'login/two-factor',
      'register',
      'forgot-password',
      'reset-password',
    ]);
  });

  it('accepts string overrides for paths', () => {
    const [{ children }] = sparkAuthRoutes({
      login: 'signin',
      register: 'signup',
    });

    expect(children.map((c: any) => c.path)).toEqual([
      'signin',
      'login/two-factor',
      'signup',
      'forgot-password',
      'reset-password',
    ]);
  });

  it('accepts object-form overrides with custom path', () => {
    const [{ children }] = sparkAuthRoutes({
      login: { path: 'auth/in' },
    });

    expect(children[0].path).toBe('auth/in');
  });

  it('exposes path constants with leading slash via SPARK_AUTH_ROUTE_PATHS provider', () => {
    const [{ providers }] = sparkAuthRoutes({
      login: 'signin',
    });

    const pathsProvider = providers!.find(
      (p: any) => p.provide && p.provide.toString().includes('SPARK_AUTH_ROUTE_PATHS'),
    );
    expect(pathsProvider!.useValue.login).toBe('/signin');
    expect(pathsProvider!.useValue.register).toBe('/register');
  });
});
