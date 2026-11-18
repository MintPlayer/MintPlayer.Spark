import { describe, expect, it } from 'vitest';
import { withSparkApps } from './apps-routes';

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

  it('refuses an install path without :slug', () => {
    expect(() => withSparkApps({ appInstall: 'install' })).toThrow(/:slug/);
  });
});
