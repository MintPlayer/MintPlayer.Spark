import { Route } from '@angular/router';
import { routes } from './app.routes';

/** Every route path under the shell, in match order, with grouping parents (`path: ''`) flattened. */
function flatten(list: Route[], prefix = ''): string[] {
  return list.flatMap(r => {
    const path = [prefix, r.path ?? ''].filter(p => p.length > 0).join('/');
    return r.children ? flatten(r.children, path) : [path];
  });
}

/**
 * The route table's order is load-bearing: `sparkRoutes()` has parameterised first segments
 * (`po/:type`, `query/:queryId`), and Angular takes the first match, so a literal route after them
 * can be shadowed. The add-on and account routes must come first.
 */
describe('QnA routes', () => {
  const paths = flatten(routes[0].children ?? []);
  const firstParameterised = paths.findIndex(p => p.includes(':'));

  it('has parameterised Spark routes, last', () => {
    expect(firstParameterised).toBeGreaterThan(0);
    expect(paths.slice(firstParameterised).every(p => p.startsWith('po') || p.startsWith('query') || p.includes(':'))).toBe(true);
  });

  it('mounts the review queue before the parameterised Spark routes', () => {
    const review = paths.indexOf('moderation/review');
    expect(review).toBeGreaterThanOrEqual(0);
    expect(review).toBeLessThan(firstParameterised);
  });

  it('mounts the account pages QnA offers, before the Spark routes, and not the ones it has no server side for', () => {
    for (const path of ['confirm-email', 'account', 'account/profile', 'account/password', 'account/two-factor', 'account/personal-data', 'sign-in', 'login', 'register']) {
      const index = paths.indexOf(path);
      expect(index, path).toBeGreaterThanOrEqual(0);
      expect(index, path).toBeLessThan(firstParameterised);
    }
    expect(paths).not.toContain('account/logins');
    expect(paths).not.toContain('account/passkeys');
  });
});
