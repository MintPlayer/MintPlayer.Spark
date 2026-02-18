import { Routes } from '@angular/router';
import { SPARK_AUTH_ROUTE_PATHS, SparkAuthRouteConfig, SparkAuthRouteEntry, SparkAuthRoutePaths } from '../models';

function resolveEntry(
  entry: SparkAuthRouteEntry | undefined,
  defaultPath: string,
): { path: string; component?: () => Promise<unknown> } {
  if (entry === undefined) {
    return { path: defaultPath };
  }
  if (typeof entry === 'string') {
    return { path: entry };
  }
  return {
    path: entry.path,
    component: entry.component ? () => Promise.resolve(entry.component) : undefined,
  };
}

export function sparkAuthRoutes(config?: SparkAuthRouteConfig): Routes {
  const login = resolveEntry(config?.login, 'login');
  const twoFactor = resolveEntry(config?.twoFactor, 'login/two-factor');
  const register = resolveEntry(config?.register, 'register');
  const forgotPassword = resolveEntry(config?.forgotPassword, 'forgot-password');
  const resetPassword = resolveEntry(config?.resetPassword, 'reset-password');

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
        {
          path: login.path,
          loadComponent: login.component
            ?? (() => import('../components/login/spark-login.component').then(m => m.SparkLoginComponent)),
        },
        {
          path: twoFactor.path,
          loadComponent: twoFactor.component
            ?? (() => import('../components/two-factor/spark-two-factor.component').then(m => m.SparkTwoFactorComponent)),
        },
        {
          path: register.path,
          loadComponent: register.component
            ?? (() => import('../components/register/spark-register.component').then(m => m.SparkRegisterComponent)),
        },
        {
          path: forgotPassword.path,
          loadComponent: forgotPassword.component
            ?? (() => import('../components/forgot-password/spark-forgot-password.component').then(m => m.SparkForgotPasswordComponent)),
        },
        {
          path: resetPassword.path,
          loadComponent: resetPassword.component
            ?? (() => import('../components/reset-password/spark-reset-password.component').then(m => m.SparkResetPasswordComponent)),
        },
      ],
    },
  ];
}
