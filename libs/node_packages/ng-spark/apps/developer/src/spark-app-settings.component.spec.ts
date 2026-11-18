import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
// eslint-disable-next-line @typescript-eslint/no-deprecated -- ng-bootstrap components emit synthetic animation props
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SparkSecretDialogService } from '@mintplayer/ng-spark/client-operations';
import { SparkAppsService, type SparkApp, type SparkWebhookCatalog } from '@mintplayer/ng-spark/apps/core';
import { SparkAppSettingsComponent } from './spark-app-settings.component';
import { SparkDeveloperAppsComponent } from './spark-developer-apps.component';

const flush = () => new Promise<void>(resolve => setTimeout(resolve, 0));
const ok = <T>(value: T) => ({ ok: true as const, value });
const refused = (status: number, error: string | null = null) => ({ ok: false as const, status, error });

async function settle(fixture: ComponentFixture<unknown>) {
  await flush();
  fixture.detectChanges();
  await fixture.whenStable();
  await flush();
  fixture.detectChanges();
}

const html = (fixture: ComponentFixture<unknown>) => fixture.nativeElement as HTMLElement;

const catalog: SparkWebhookCatalog = {
  events: [{ name: 'report.completed', summary: 'A report completed.', permission: 'reports:read' }],
  permissions: ['reports:read', 'reports:write'],
};

const app: SparkApp = {
  slug: 'ci-bot', name: 'CI bot', description: null, homepageUrl: null, ownerKind: 'user', ownerKey: 'Users/1', clientId: 'app-ci-bot',
  public: false, permissions: ['reports:read'], events: ['report.completed'], webhookEndpointId: null, webhookUrl: null, setupUrl: null,
  keys: [{ keyId: '0123456789abcdef', fingerprint: 'SHA256:abc', createdUtc: '2026-11-18T10:00:00Z' }], createdUtc: '2026-11-18T09:00:00Z',
};

function configure(api: object) {
  const secrets = { show: vi.fn() };
  TestBed.configureTestingModule({
    providers: [
      // eslint-disable-next-line @typescript-eslint/no-deprecated
      provideNoopAnimations(),
      provideRouter([]),
      { provide: SparkAppsService, useValue: api },
      { provide: SparkSecretDialogService, useValue: secrets },
      { provide: SparkLanguageService, useValue: { t: (key: string) => key.replace('apps.spa.', '') } },
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['slug', 'ci-bot']]) } } },
    ],
  });
  const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  return { secrets, navigate };
}

function settingsApi(extra: object = {}) {
  return {
    app: vi.fn().mockResolvedValue(ok(app)),
    catalog: vi.fn().mockResolvedValue(ok(catalog)),
    appInstallations: vi.fn().mockResolvedValue(ok([])),
    ...extra,
  };
}

describe('SparkAppSettingsComponent', () => {
  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it('shows the client id, the keys and the private hint of an App its manager opens', async () => {
    configure(settingsApi());
    const fixture = TestBed.createComponent(SparkAppSettingsComponent);
    await settle(fixture);

    expect(html(fixture).querySelector('.spark-app-client-id')!.textContent).toBe('app-ci-bot');
    expect(html(fixture).querySelectorAll('.spark-app-key')).toHaveLength(1);
    expect(html(fixture).querySelector('.spark-app-install-link')!.textContent).toContain('ci-bot');
  });

  it('says not found for the public view of someone else\'s App, and for a 404', async () => {
    const { slug, name, description, homepageUrl, ownerKind, ownerKey, permissions, events } = app;
    configure(settingsApi({ app: vi.fn().mockResolvedValue(ok({ slug, name, description, homepageUrl, ownerKind, ownerKey, permissions, events })) }));
    const publicView = TestBed.createComponent(SparkAppSettingsComponent);
    await settle(publicView);
    expect(publicView.componentInstance.app()).toBeNull();
    expect(publicView.componentInstance.error()).toBe('app.notFound');

    TestBed.resetTestingModule();
    configure(settingsApi({ app: vi.fn().mockResolvedValue(refused(500)) }));
    const failed = TestBed.createComponent(SparkAppSettingsComponent);
    await settle(failed);
    // Anything but a 404 is a failure to load.
    expect(failed.componentInstance.error()).toBe('loadFailed');
  });

  it('shows a new webhook secret once after a save, and the server\'s refusal otherwise', async () => {
    const updateApp = vi.fn()
      .mockResolvedValueOnce(ok({ app: { ...app, webhookUrl: 'https://ci.example/hook' }, webhookSecret: 'whsec_x' }))
      .mockResolvedValueOnce(refused(400, 'The URL must use https.'));
    const { secrets } = configure(settingsApi({ updateApp }));
    const fixture = TestBed.createComponent(SparkAppSettingsComponent);
    await settle(fixture);

    await fixture.componentInstance.save({ ...app, webhookUrl: 'https://ci.example/hook' });
    expect(secrets.show).toHaveBeenCalledWith(expect.objectContaining({ value: 'whsec_x' }));
    expect(fixture.componentInstance.notice()).toBe('app.saved');
    expect(fixture.componentInstance.app()!.webhookUrl).toBe('https://ci.example/hook');

    await fixture.componentInstance.save({ ...app, webhookUrl: 'http://ci.example/hook' });
    expect(fixture.componentInstance.error()).toBe('The URL must use https.');
    expect(fixture.componentInstance.busy()).toBe(false);
  });

  it('shows a generated private key once and lists it; deleting a key asks first', async () => {
    const created = { key: { keyId: 'fedcba9876543210', fingerprint: 'SHA256:def', createdUtc: '2026-11-18T11:00:00Z' }, privateKeyPem: '-----BEGIN PRIVATE KEY-----' };
    const deleteKey = vi.fn().mockResolvedValue(ok({ ...app, keys: [] }));
    const { secrets } = configure(settingsApi({ generateKey: vi.fn().mockResolvedValue(ok(created)), deleteKey }));
    const fixture = TestBed.createComponent(SparkAppSettingsComponent);
    await settle(fixture);

    await fixture.componentInstance.generateKey(app);
    expect(secrets.show).toHaveBeenCalledWith(expect.objectContaining({ value: created.privateKeyPem }));
    expect(fixture.componentInstance.app()!.keys.map(k => k.keyId)).toEqual(['0123456789abcdef', 'fedcba9876543210']);

    vi.stubGlobal('confirm', vi.fn().mockReturnValueOnce(false).mockReturnValueOnce(true));
    await fixture.componentInstance.deleteKey(app, app.keys[0]);
    expect(deleteKey).not.toHaveBeenCalled();
    await fixture.componentInstance.deleteKey(app, app.keys[0]);
    expect(deleteKey).toHaveBeenCalledWith('ci-bot', '0123456789abcdef');
    expect(fixture.componentInstance.app()!.keys).toEqual([]);
    expect(fixture.componentInstance.notice()).toBe('app.keyDeleted');
  });

  it('goes back to the list after deleting the App, and stays when the delete is refused', async () => {
    const deleteApp = vi.fn().mockResolvedValueOnce(refused(409, 'The App changed meanwhile; reload and try again.')).mockResolvedValueOnce(ok(true));
    const { navigate } = configure(settingsApi({ deleteApp }));
    vi.stubGlobal('confirm', vi.fn().mockReturnValue(true));
    const fixture = TestBed.createComponent(SparkAppSettingsComponent);
    await settle(fixture);

    await fixture.componentInstance.remove(app);
    expect(navigate).not.toHaveBeenCalled();
    expect(fixture.componentInstance.error()).toBe('The App changed meanwhile; reload and try again.');

    await fixture.componentInstance.remove(app);
    expect(navigate).toHaveBeenCalledTimes(1);
  });
});

describe('SparkDeveloperAppsComponent', () => {
  afterEach(() => vi.restoreAllMocks());

  const owners = [{ kind: 'user', key: 'Users/1', displayName: 'Ann' }, { kind: 'organization', key: 'Organizations/1', displayName: 'Acme' }];

  it('lists the first owner\'s Apps, and another owner\'s when chosen', async () => {
    const apps = vi.fn().mockResolvedValueOnce(ok([app])).mockResolvedValueOnce(ok([]));
    configure({ owners: vi.fn().mockResolvedValue(ok(owners)), catalog: vi.fn().mockResolvedValue(ok(catalog)), apps });
    const fixture = TestBed.createComponent(SparkDeveloperAppsComponent);
    await settle(fixture);

    expect(apps).toHaveBeenCalledWith(owners[0]);
    expect(fixture.componentInstance.apps()).toEqual([app]);
    await fixture.componentInstance.choose(owners[1]);
    expect(apps).toHaveBeenLastCalledWith(owners[1]);
    expect(fixture.componentInstance.apps()).toEqual([]);
  });

  it('says it failed to load when the owners or the catalog do not load', async () => {
    const apps = vi.fn();
    configure({ owners: vi.fn().mockResolvedValue(refused(500)), catalog: vi.fn().mockResolvedValue(ok(catalog)), apps });
    const fixture = TestBed.createComponent(SparkDeveloperAppsComponent);
    await settle(fixture);

    expect(fixture.componentInstance.error()).toBe('loadFailed');
    expect(fixture.componentInstance.loading()).toBe(false);
    expect(apps).not.toHaveBeenCalled();
  });

  it('registers an App for the chosen owner, shows its webhook secret once and opens it', async () => {
    const createApp = vi.fn()
      .mockResolvedValueOnce(refused(409, "The slug 'ci-bot' is taken."))
      .mockResolvedValueOnce(ok({ app, webhookSecret: 'whsec_y' }));
    const { secrets, navigate } = configure({
      owners: vi.fn().mockResolvedValue(ok(owners)), catalog: vi.fn().mockResolvedValue(ok(catalog)), apps: vi.fn().mockResolvedValue(ok([])), createApp,
    });
    const fixture = TestBed.createComponent(SparkDeveloperAppsComponent);
    await settle(fixture);
    await fixture.componentInstance.choose(owners[1]);

    await fixture.componentInstance.register(app);
    expect(fixture.componentInstance.error()).toBe("The slug 'ci-bot' is taken.");
    expect(navigate).not.toHaveBeenCalled();

    await fixture.componentInstance.register(app);
    expect(createApp).toHaveBeenLastCalledWith(expect.objectContaining({ slug: 'ci-bot', ownerKind: 'organization', ownerKey: 'Organizations/1' }));
    expect(secrets.show).toHaveBeenCalledWith(expect.objectContaining({ value: 'whsec_y' }));
    expect(navigate).toHaveBeenCalledTimes(1);
    expect(navigate.mock.calls[0][0]).toContain('ci-bot');
  });
});
