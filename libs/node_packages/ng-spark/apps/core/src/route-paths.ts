import { inject } from '@angular/core';
import { SPARK_AUTH_ROUTE_PATHS } from '@mintplayer/ng-spark/auth/models';

/** Where `withSparkApps()` mounts the pages unless told otherwise. */
export const SPARK_APPS_DEFAULT_PATHS = {
  webhooks: '/account/webhooks',
  developerApps: '/account/apps',
  appInstallations: '/account/installations',
  appInstall: '/apps/:slug/install',
} as const;

/** The pages' links to each other, from the paths `sparkAuthRoutes(withSparkApps())` mounted. */
export interface SparkAppsLinks {
  /** A webhook's delivery log. */
  deliveries(endpointId: string): string;
  webhooks(): string;
  developerApps(): string;
  /** An App's settings. */
  app(slug: string): string;
  appInstallations(): string;
  /** The consent page others open to install the App. */
  install(slug: string): string;
}

export function injectAppsLinks(): SparkAppsLinks {
  const paths = inject(SPARK_AUTH_ROUTE_PATHS, { optional: true }) ?? {};
  const webhooks = paths.webhooks ?? SPARK_APPS_DEFAULT_PATHS.webhooks;
  const developerApps = paths.developerApps ?? SPARK_APPS_DEFAULT_PATHS.developerApps;
  const appInstallations = paths.appInstallations ?? SPARK_APPS_DEFAULT_PATHS.appInstallations;
  const appInstall = paths.appInstall ?? SPARK_APPS_DEFAULT_PATHS.appInstall;
  return {
    deliveries: id => `${webhooks}/${encodeURIComponent(id)}`,
    webhooks: () => webhooks,
    developerApps: () => developerApps,
    app: slug => `${developerApps}/${encodeURIComponent(slug)}`,
    appInstallations: () => appInstallations,
    install: slug => appInstall.replace(':slug', encodeURIComponent(slug)),
  };
}
