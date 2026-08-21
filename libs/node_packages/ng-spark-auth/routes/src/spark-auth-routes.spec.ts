import { SPARK_AUTH_ROUTE_PATHS } from '@mintplayer/ng-spark-auth/models';
import { sparkAuthRoutes } from './spark-auth-routes';

/**
 * Pins which pages `sparkAuthRoutes` emits per mode. The local-credential pages form a star centred
 * on login, so they are emitted and dropped as one group — dropping any proper subset would leave a
 * surviving template linking to a route that no longer exists.
 */
describe('sparkAuthRoutes', () => {
  const childPaths = (config?: Parameters<typeof sparkAuthRoutes>[0]) =>
    sparkAuthRoutes(config)[0].children.map((child: any) => child.path);

  it('emits every local-credential page by default', () => {
    // Red if the default ever shifts — an app that never opted in must be unaffected.
    expect(childPaths().sort()).toEqual(
      ['login', 'login/two-factor', 'register', 'forgot-password', 'reset-password'].sort(),
    );
  });

  it('emits no local-credential page when disabled, only the sign-in landing', () => {
    // The landing page is what makes 'disabled' usable — without it there would be no route to
    // point loginUrl at, and the guard would redirect into nothing.
    expect(childPaths({ localCredentials: 'disabled' })).toEqual(['sign-in']);
  });

  it('does not emit the sign-in landing in full mode', () => {
    // Emitting it by default would change the routes of every app that never opted in.
    expect(childPaths()).not.toContain('sign-in');
  });

  it('drops only registration in sign-in-only mode', () => {
    // Admin-provisioned accounts still need password sign-in and password recovery.
    const paths = childPaths({ localCredentials: 'sign-in-only' });

    expect(paths).not.toContain('register');
    expect(paths.sort()).toEqual(
      ['sign-in', 'login', 'login/two-factor', 'forgot-password', 'reset-password'].sort(),
    );
  });

  it('provides every route path regardless of mode', () => {
    // SPARK_AUTH_ROUTE_PATHS stays Required, so a consumer's own template can still reference any
    // path without a null check. Safe precisely because the family drops as a unit.
    const provider = sparkAuthRoutes({ localCredentials: 'disabled' })[0].providers
      .find((p: any) => p.provide === SPARK_AUTH_ROUTE_PATHS);

    expect(provider.useValue).toEqual({
      signIn: '/sign-in',
      login: '/login',
      twoFactor: '/login/two-factor',
      register: '/register',
      forgotPassword: '/forgot-password',
      resetPassword: '/reset-password',
    });
  });

  it('honours a renamed path', () => {
    expect(childPaths({ login: 'sign-in' })).toContain('sign-in');
  });
});
