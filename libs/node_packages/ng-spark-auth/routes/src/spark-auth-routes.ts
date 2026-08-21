import {
  SPARK_AUTH_ROUTE_PATHS,
  SPARK_EXTERNAL_PROVIDERS,
  SparkAuthRouteEntries,
  SparkAuthRouteEntry,
  SparkAuthRoutePaths,
  SparkExternalProviderPresentation,
} from '@mintplayer/ng-spark-auth/models';

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
 * One opt-in group of authentication pages. Produced by `withLocalLogin()`, `withRegistration()` and
 * `withExternalLogin()`; not constructible by consumers, which is what keeps the set of mountable
 * pages a decision this library makes rather than a shape an application can invent.
 */
export interface SparkAuthRoutesFeature {
  readonly children: Child[];
  readonly paths: SparkAuthRoutePaths;
  readonly providers?: SparkExternalProviderPresentation[];
}

/** Path override for the sign-in landing page. Named to stay clear of the core package's
 * `SparkExternalLoginOptions`, which is about the sign-in *call* rather than the route. */
export type SparkExternalLoginRouteOptions = Pick<SparkAuthRouteEntries, 'signIn'>;

/**
 * The routes for Spark's authentication pages.
 *
 * **Nothing is mounted unless a feature asks for it.** Passing no features emits no pages at all —
 * opt-in enforced by construction rather than by a flag, matching `provideHttpClient(withFetch())`
 * and `provideRouter(withComponentInputBinding())`. Before this the pages mounted by default and an
 * application had to opt *out*, so every app shipped a registration form whether or not it wanted
 * one.
 *
 * ```ts
 * sparkAuthRoutes(
 *   withLocalLogin(),
 *   withRegistration(),
 *   withExternalLogin(githubProvider(), facebookProvider()),
 * )
 * ```
 *
 * The `import()` expressions live *inside* each feature. That placement is the point: a bundler
 * decides whether to emit a lazy chunk from whether the `import()` call site is reachable, not from
 * whether the route object referencing it survives — so filtering a children array after the fact
 * would still ship every page. A page nobody opted into has no reachable `import()` at all.
 */
export function sparkAuthRoutes(...features: SparkAuthRoutesFeature[]): any[] {
  const children: Child[] = [];
  const paths: SparkAuthRoutePaths = {};
  const externalProviders: SparkExternalProviderPresentation[] = [];

  for (const feature of features) {
    children.push(...feature.children);
    Object.assign(paths, feature.paths);
    if (feature.providers) externalProviders.push(...feature.providers);
  }

  return [
    {
      path: '',
      providers: [
        { provide: SPARK_AUTH_ROUTE_PATHS, useValue: paths },
        { provide: SPARK_EXTERNAL_PROVIDERS, useValue: externalProviders },
      ],
      children,
    },
  ];
}

/**
 * Mounts the email/password family: login, two-factor, forgot-password, reset-password.
 *
 * These four are one feature rather than four because they form a star centred on the login page and
 * every template dereferences its siblings unconditionally — removing any proper subset leaves a
 * dangling link. Registration is genuinely separable, and is its own feature.
 *
 * Two-factor belongs here because it is reachable only from the login page's `RequiresTwoFactor`
 * branch and posts to the same `/login` endpoint: it is part of password sign-in, not of
 * authentication in general.
 */
export function withLocalLogin(
  entries?: Pick<SparkAuthRouteEntries, 'login' | 'twoFactor' | 'forgotPassword' | 'resetPassword'>,
): SparkAuthRoutesFeature {
  const login = entryPath(entries?.login, 'login');
  const twoFactor = entryPath(entries?.twoFactor, 'login/two-factor');
  const forgotPassword = entryPath(entries?.forgotPassword, 'forgot-password');
  const resetPassword = entryPath(entries?.resetPassword, 'reset-password');

  return {
    paths: {
      login: '/' + login,
      twoFactor: '/' + twoFactor,
      forgotPassword: '/' + forgotPassword,
      resetPassword: '/' + resetPassword,
    },
    children: [
      child(entries?.login, login,
        () => import('@mintplayer/ng-spark-auth/login').then(m => m.SparkLoginComponent)),
      child(entries?.twoFactor, twoFactor,
        () => import('@mintplayer/ng-spark-auth/two-factor').then(m => m.SparkTwoFactorComponent)),
      child(entries?.forgotPassword, forgotPassword,
        () => import('@mintplayer/ng-spark-auth/forgot-password').then(m => m.SparkForgotPasswordComponent)),
      child(entries?.resetPassword, resetPassword,
        () => import('@mintplayer/ng-spark-auth/reset-password').then(m => m.SparkResetPasswordComponent)),
    ],
  };
}

/**
 * Mounts the self-service registration page.
 *
 * Separate from {@link withLocalLogin} because the two decisions genuinely are separate: an
 * application whose accounts are provisioned by an administrator still needs password sign-in and
 * password reset. Mount it only alongside a server whose `SparkLocalCredentials` is `Full` — with
 * `SignInOnly` the endpoint it posts to is not mapped.
 */
export function withRegistration(entry?: SparkAuthRouteEntry): SparkAuthRoutesFeature {
  const register = entryPath(entry, 'register');

  return {
    paths: { register: '/' + register },
    children: [
      child(entry, register,
        () => import('@mintplayer/ng-spark-auth/register').then(m => m.SparkRegisterComponent)),
    ],
  };
}

/**
 * Mounts the sign-in landing page — a button per external provider the *server* reports.
 *
 * Accepts provider declarations and, in any position, one options object for the page's own path;
 * the two shapes are disjoint, so `withExternalLogin(githubProvider(), facebookProvider())` and
 * `withExternalLogin({ signIn: 'landing' }, githubProvider())` both read naturally.
 *
 * Declaring no providers is valid and useful: the page still renders whatever
 * `GET /spark/auth/capabilities` reports, using default buttons.
 */
export function withExternalLogin(
  ...providersOrOptions: (SparkExternalProviderPresentation | SparkExternalLoginRouteOptions)[]
): SparkAuthRoutesFeature {
  const providers = providersOrOptions.filter(isProvider);
  const options = providersOrOptions.find((p): p is SparkExternalLoginRouteOptions => !isProvider(p));
  const signIn = entryPath(options?.signIn, 'sign-in');

  return {
    paths: { signIn: '/' + signIn },
    providers,
    children: [
      child(options?.signIn, signIn,
        () => import('@mintplayer/ng-spark-auth/sign-in').then(m => m.SparkSignInComponent)),
    ],
  };
}

function isProvider(
  value: SparkExternalProviderPresentation | SparkExternalLoginRouteOptions,
): value is SparkExternalProviderPresentation {
  return typeof (value as SparkExternalProviderPresentation).scheme === 'string';
}

/**
 * Presentation for one provider's button, keyed by the scheme the server reports.
 *
 * The generic form. `githubProvider()` and friends are this with a scheme and an icon filled in —
 * they exist so the common case does not require knowing the scheme string, not because the library
 * has an opinion about which providers exist.
 */
export function externalProvider(
  scheme: string,
  presentation?: Omit<SparkExternalProviderPresentation, 'scheme'>,
): SparkExternalProviderPresentation {
  return { scheme, ...presentation };
}

export function githubProvider(
  presentation?: Omit<SparkExternalProviderPresentation, 'scheme'>,
): SparkExternalProviderPresentation {
  return externalProvider('GitHub', { iconClass: 'bi bi-github', ...presentation });
}

export function googleProvider(
  presentation?: Omit<SparkExternalProviderPresentation, 'scheme'>,
): SparkExternalProviderPresentation {
  return externalProvider('Google', { iconClass: 'bi bi-google', ...presentation });
}

export function facebookProvider(
  presentation?: Omit<SparkExternalProviderPresentation, 'scheme'>,
): SparkExternalProviderPresentation {
  return externalProvider('Facebook', { iconClass: 'bi bi-facebook', ...presentation });
}

export function microsoftProvider(
  presentation?: Omit<SparkExternalProviderPresentation, 'scheme'>,
): SparkExternalProviderPresentation {
  return externalProvider('Microsoft', { iconClass: 'bi bi-microsoft', ...presentation });
}
