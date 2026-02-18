import { Type } from '@angular/core';
import { Routes } from '@angular/router';
import { SPARK_AUTH_ROUTE_PATHS, SparkAuthRouteConfig, SparkAuthRouteEntry, SparkAuthRoutePaths } from '../models';

interface ResolvedEntry {
  path: string;
  loadComponent: () => Promise<Type<unknown>>;
}

function resolveEntry(
  entry: SparkAuthRouteEntry | undefined,
  defaultPath: string,
  defaultLoader: () => Promise<Type<unknown>>,
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

export function sparkAuthRoutes(config?: SparkAuthRouteConfig): Routes {
  const login = resolveEntry(config?.login, 'login',
    () => import('../components/login/spark-login.component').then(m => m.SparkLoginComponent));
  const twoFactor = resolveEntry(config?.twoFactor, 'login/two-factor',
    () => import('../components/two-factor/spark-two-factor.component').then(m => m.SparkTwoFactorComponent));
  const register = resolveEntry(config?.register, 'register',
    () => import('../components/register/spark-register.component').then(m => m.SparkRegisterComponent));
  const forgotPassword = resolveEntry(config?.forgotPassword, 'forgot-password',
    () => import('../components/forgot-password/spark-forgot-password.component').then(m => m.SparkForgotPasswordComponent));
  const resetPassword = resolveEntry(config?.resetPassword, 'reset-password',
    () => import('../components/reset-password/spark-reset-password.component').then(m => m.SparkResetPasswordComponent));

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
