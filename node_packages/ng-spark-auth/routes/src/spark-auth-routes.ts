import { SPARK_AUTH_ROUTE_PATHS, SparkAuthRouteConfig, SparkAuthRouteEntry, SparkAuthRoutePaths } from '@mintplayer/ng-spark-auth/models';

interface ResolvedEntry {
  path: string;
  loadComponent: () => Promise<any>;
}

function resolveEntry(
  entry: SparkAuthRouteEntry | undefined,
  defaultPath: string,
  defaultLoader: () => Promise<any>,
): ResolvedEntry {
  if (entry === undefined || typeof entry === 'string') {
    return {
      path: typeof entry === 'string' ? entry : defaultPath,
      loadComponent: defaultLoader,
    };
  }
  return {
    path: entry.path,
    loadComponent: entry.component
      ? () => Promise.resolve(entry.component!)
      : defaultLoader,
  };
}

export function sparkAuthRoutes(config?: SparkAuthRouteConfig): any[] {
  const login = resolveEntry(config?.login, 'login',
    () => import('@mintplayer/ng-spark-auth/login').then(m => m.SparkLoginComponent));
  const twoFactor = resolveEntry(config?.twoFactor, 'login/two-factor',
    () => import('@mintplayer/ng-spark-auth/two-factor').then(m => m.SparkTwoFactorComponent));
  const register = resolveEntry(config?.register, 'register',
    () => import('@mintplayer/ng-spark-auth/register').then(m => m.SparkRegisterComponent));
  const forgotPassword = resolveEntry(config?.forgotPassword, 'forgot-password',
    () => import('@mintplayer/ng-spark-auth/forgot-password').then(m => m.SparkForgotPasswordComponent));
  const resetPassword = resolveEntry(config?.resetPassword, 'reset-password',
    () => import('@mintplayer/ng-spark-auth/reset-password').then(m => m.SparkResetPasswordComponent));

  const paths: SparkAuthRoutePaths = {
    login: '/' + login.path,
    twoFactor: '/' + twoFactor.path,
    register: '/' + register.path,
    forgotPassword: '/' + forgotPassword.path,
    resetPassword: '/' + resetPassword.path,
  };

  return [
    {
      path: '',
      providers: [
        { provide: SPARK_AUTH_ROUTE_PATHS, useValue: paths },
      ],
      children: [
        { path: login.path, loadComponent: login.loadComponent },
        { path: twoFactor.path, loadComponent: twoFactor.loadComponent },
        { path: register.path, loadComponent: register.loadComponent },
        { path: forgotPassword.path, loadComponent: forgotPassword.loadComponent },
        { path: resetPassword.path, loadComponent: resetPassword.loadComponent },
      ],
    },
  ];
}
