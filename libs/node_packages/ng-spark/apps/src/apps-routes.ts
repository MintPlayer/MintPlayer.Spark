import { HttpClient } from '@angular/common/http';
import { inject } from '@angular/core';
import type { CanActivateFn } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { SPARK_APPS_API } from '@mintplayer/ng-spark/apps/core';
import { sparkAuthGuard } from '@mintplayer/ng-spark/auth/guards';
import type { SparkAccountOverviewLink, SparkAuthRouteEntry, SparkAuthRoutePaths } from '@mintplayer/ng-spark/auth/models';
import type { SparkAuthRoutesFeature } from '@mintplayer/ng-spark/auth/routes';

type Child = SparkAuthRoutesFeature['children'][number];
type Loader = () => Promise<any>;

export interface SparkAppsRoutesOptions {
  /** Defaults to `[sparkAuthGuard]`; `[]` under an already guarded parent. */
  canActivate?: CanActivateFn[];
  /** Default `account/webhooks`; `<path>/:id` is a webhook's delivery log. */
  webhooks?: SparkAuthRouteEntry;
  /** Default `account/apps`; `<path>/:slug` is an App's settings. Leave out with `apps: false` when the site has no Apps. */
  developerApps?: SparkAuthRouteEntry;
  /** Default `account/installations`. */
  appInstallations?: SparkAuthRouteEntry;
  /** Default `apps/:slug/install`; must hold `:slug`. */
  appInstall?: SparkAuthRouteEntry;
  /**
   * False for a site with direct webhooks only (no `apps.EnableApps()` on the server): mounts the webhooks pages
   * and leaves out the Apps, installation and consent pages. Default true.
   */
  apps?: boolean;
}

function entryPath(entry: SparkAuthRouteEntry | undefined, defaultPath: string): string {
  if (entry === undefined) return defaultPath;
  return typeof entry === 'string' ? entry : entry.path;
}

/**
 * Whether the server maps the webhooks API: a site can mount the pages and still leave webhooks switched off (CodeCoverage's
 * `CodeCoverage:Webhooks:Enabled`), and then every `/spark/apps` route is a 404. The catalog is the one anonymous route, so
 * the account overview asks it before it offers the link.
 */
export function webhooksServed(): Promise<boolean> {
  return firstValueFrom(inject(HttpClient).get(`${SPARK_APPS_API}/webhooks/catalog`)).then(() => true, () => false);
}

/** An explicitly supplied component wins over the library's lazy import, as in `sparkAuthRoutes()`. */
function child(entry: SparkAuthRouteEntry | undefined, path: string, loader: Loader, guard: CanActivateFn[]): Child {
  const component = typeof entry === 'object' && entry.component ? entry.component : undefined;
  return {
    path,
    loadComponent: component ? () => Promise.resolve(component) : loader,
    ...(guard.length ? { canActivate: guard } : {}),
  };
}

/**
 * The webhooks and Apps pages (#691, `docs/apps_webhooks_PRD.md` M7), as one `sparkAuthRoutes()` feature:
 *
 * ```ts
 * sparkAuthRoutes(withLocalLogin(), withAccount(), withSparkApps())
 * ```
 *
 * Each page keeps its own `import()` (its own secondary entry point), so a page that is not mounted is never
 * bundled. Every page is for a signed-in user; the guard sends anyone else to sign in and back, which the consent
 * page needs most: an App's install link is shared with people who may not be signed in yet.
 */
export function withSparkApps(options?: SparkAppsRoutesOptions): SparkAuthRoutesFeature {
  const guard = options?.canActivate ?? [sparkAuthGuard];
  const withApps = options?.apps ?? true;
  const webhooks = entryPath(options?.webhooks, 'account/webhooks');
  const paths: SparkAuthRoutePaths = { webhooks: '/' + webhooks };
  const accountLinks: SparkAccountOverviewLink[] = [
    { key: 'webhooks', path: '/' + webhooks, label: { en: 'Webhooks', fr: 'Webhooks', nl: 'Webhooks' }, available: webhooksServed },
  ];
  const webhooksPage: Loader = () => import('@mintplayer/ng-spark/apps/webhooks').then(m => m.SparkWebhooksComponent);
  const children: Child[] = [
    child(options?.webhooks, webhooks, webhooksPage, guard),
    child(undefined, webhooks + '/:id', () => import('@mintplayer/ng-spark/apps/webhooks').then(m => m.SparkWebhookDeliveriesComponent), guard),
  ];

  if (withApps) {
    const developerApps = entryPath(options?.developerApps, 'account/apps');
    const appInstallations = entryPath(options?.appInstallations, 'account/installations');
    const appInstall = entryPath(options?.appInstall, 'apps/:slug/install');
    if (!appInstall.includes(':slug')) throw new Error(`withSparkApps(): the appInstall path '${appInstall}' must hold :slug.`);
    Object.assign(paths, { developerApps: '/' + developerApps, appInstallations: '/' + appInstallations, appInstall: '/' + appInstall });
    accountLinks.push(
      { key: 'developerApps', path: '/' + developerApps, label: { en: 'Your Apps', fr: 'Vos Apps', nl: 'Je Apps' } },
      { key: 'appInstallations', path: '/' + appInstallations, label: { en: 'Installed Apps', fr: 'Apps installées', nl: 'Geïnstalleerde Apps' } },
    );
    children.push(
      child(options?.developerApps, developerApps, () => import('@mintplayer/ng-spark/apps/developer').then(m => m.SparkDeveloperAppsComponent), guard),
      child(undefined, developerApps + '/:slug', () => import('@mintplayer/ng-spark/apps/developer').then(m => m.SparkAppSettingsComponent), guard),
      child(options?.appInstallations, appInstallations, () => import('@mintplayer/ng-spark/apps/installations').then(m => m.SparkAppInstallationsComponent), guard),
      child(options?.appInstall, appInstall, () => import('@mintplayer/ng-spark/apps/installations').then(m => m.SparkAppInstallComponent), guard),
    );
  }
  return { children, paths, accountLinks };
}
