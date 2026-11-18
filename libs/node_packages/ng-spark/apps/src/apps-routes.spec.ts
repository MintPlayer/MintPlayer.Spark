import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { webhooksServed, withSparkApps } from './apps-routes';

describe('withSparkApps', () => {
  it('mounts every page under the default paths, guarded, with account-overview links', () => {
    const feature = withSparkApps();
    expect(feature.children.map(c => c.path)).toEqual([
      'account/webhooks', 'account/webhooks/:id', 'account/apps', 'account/apps/:slug', 'account/installations', 'apps/:slug/install',
    ]);
    expect(feature.children.every(c => (c.canActivate?.length ?? 0) === 1)).toBe(true);
    expect(feature.paths).toEqual({
      webhooks: '/account/webhooks', developerApps: '/account/apps', appInstallations: '/account/installations', appInstall: '/apps/:slug/install',
    });
    expect(feature.accountLinks!.map(l => l.key)).toEqual(['webhooks', 'developerApps', 'appInstallations']);
  });

  it('a site with direct webhooks only mounts the webhooks pages', () => {
    const feature = withSparkApps({ apps: false, webhooks: 'settings/hooks', canActivate: [] });
    expect(feature.children.map(c => c.path)).toEqual(['settings/hooks', 'settings/hooks/:id']);
    expect(feature.children.every(c => c.canActivate === undefined)).toBe(true);
    expect(feature.paths).toEqual({ webhooks: '/settings/hooks' });
  });

  // #691: a site can mount the pages with webhooks switched off server-side; then the catalog, like every /spark/apps route, is a 404.
  it('offers the webhooks link only when the server answers the anonymous catalog', async () => {
    const link = withSparkApps({ apps: false }).accountLinks!.find(l => l.key === 'webhooks')!;
    expect(link.available).toBe(webhooksServed);

    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const http = TestBed.inject(HttpTestingController);
    const served = TestBed.runInInjectionContext(webhooksServed);
    http.expectOne('/spark/apps/webhooks/catalog').flush({ openapi: '3.1.0' });
    await expect(served).resolves.toBe(true);

    const off = TestBed.runInInjectionContext(webhooksServed);
    http.expectOne('/spark/apps/webhooks/catalog').flush('', { status: 404, statusText: 'Not Found' });
    await expect(off).resolves.toBe(false);
    http.verify();
  });

  it('refuses an install path without :slug', () => {
    expect(() => withSparkApps({ appInstall: 'install' })).toThrow(/:slug/);
  });
});
