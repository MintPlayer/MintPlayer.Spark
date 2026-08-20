import { SPARK_AUTH_ROUTE_PATHS, SparkAuthRouteConfig, SparkAuthRouteEntry, SparkAuthRoutePaths } from '@mintplayer/ng-spark-auth/models';

type Loader = () => Promise<any>;

interface Child {
  path: string;
  loadComponent: Loader;
}

/** The routed path for an entry, independent of how its component is loaded. */
function entryPath(entry: SparkAuthRouteEntry | undefined, defaultPath: string): string {
  if (entry === undefined) return defaultPath;
  return typeof entry === 'string' ? entry : entry.path;
}

/** An explicitly supplied component wins over the library's lazy import. */
function child(entry: SparkAuthRouteEntry | undefined, path: string, defaultLoader: Loader): Child {
  const component = typeof entry === 'object' && entry.component ? entry.component : undefined;
  return { path, loadComponent: component ? () => Promise.resolve(component) : defaultLoader };
}

/**
 * The routes for Spark's authentication pages.
 *
 * `config.localCredentials` chooses how much of the email/password family to route, mirroring the
 * server's `SparkLocalCredentials`. `GET /spark/auth/capabilities` reports what the server is
 * actually running, so the two can be checked against each other.
 *
 * The `import()` expressions live *inside* the branches that need them. That placement is the point:
 * a bundler decides whether to emit a lazy chunk from whether the `import()` call site is reachable,
 * not from whether the route object referencing it survives — so filtering the children array after
 * the fact would still ship every page. Excluded pages must have no reachable `import()` at all.
 *
 * `SPARK_AUTH_ROUTE_PATHS` is still provided in full. The excluded pages are excluded together, so
 * nothing that survives can link to something that does not, and the token stays `Required`.
 */
export function sparkAuthRoutes(config?: SparkAuthRouteConfig): any[] {
  const mode = config?.localCredentials ?? 'full';

  const paths: SparkAuthRoutePaths = {
    signIn: '/' + entryPath(config?.signIn, 'sign-in'),
    login: '/' + entryPath(config?.login, 'login'),
    twoFactor: '/' + entryPath(config?.twoFactor, 'login/two-factor'),
    register: '/' + entryPath(config?.register, 'register'),
    forgotPassword: '/' + entryPath(config?.forgotPassword, 'forgot-password'),
    resetPassword: '/' + entryPath(config?.resetPassword, 'reset-password'),
  };

  const children: Child[] = [];

  // Only when local credentials are limited. In 'full' mode the login page is already the landing
  // page, and emitting a second one would change the routes of every app that never opted in.
  if (mode !== 'full') {
    children.push(
      child(config?.signIn, entryPath(config?.signIn, 'sign-in'),
        () => import('@mintplayer/ng-spark-auth/sign-in').then(m => m.SparkSignInComponent)),
    );
  }

  if (mode !== 'disabled') {
    children.push(
      child(config?.login, entryPath(config?.login, 'login'),
        () => import('@mintplayer/ng-spark-auth/login').then(m => m.SparkLoginComponent)),
      // Reachable only from the login page's RequiresTwoFactor branch, and it posts to the same
      // /login endpoint — so it belongs to password sign-in, not to authentication in general.
      child(config?.twoFactor, entryPath(config?.twoFactor, 'login/two-factor'),
        () => import('@mintplayer/ng-spark-auth/two-factor').then(m => m.SparkTwoFactorComponent)),
      child(config?.forgotPassword, entryPath(config?.forgotPassword, 'forgot-password'),
        () => import('@mintplayer/ng-spark-auth/forgot-password').then(m => m.SparkForgotPasswordComponent)),
      child(config?.resetPassword, entryPath(config?.resetPassword, 'reset-password'),
        () => import('@mintplayer/ng-spark-auth/reset-password').then(m => m.SparkResetPasswordComponent)),
    );
  }

  if (mode === 'full') {
    children.push(
      child(config?.register, entryPath(config?.register, 'register'),
        () => import('@mintplayer/ng-spark-auth/register').then(m => m.SparkRegisterComponent)),
    );
  }

  return [
    {
      path: '',
      providers: [
        { provide: SPARK_AUTH_ROUTE_PATHS, useValue: paths },
      ],
      children,
    },
  ];
}
