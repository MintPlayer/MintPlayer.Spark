import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
// eslint-disable-next-line @typescript-eslint/no-deprecated -- bs-alert emits synthetic animation props
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SparkAppsService, type SparkAppInstallation, type SparkAppInstallPrompt } from '@mintplayer/ng-spark/apps/core';
import { SPARK_APPS_LEAVE_SITE, SparkAppInstallComponent } from './spark-app-install.component';
import { SparkAppInstallationsComponent } from './spark-app-installations.component';

const flush = () => new Promise<void>(resolve => setTimeout(resolve, 0));
const ok = <T>(value: T) => ({ ok: true as const, value });

async function settle(fixture: ComponentFixture<unknown>) {
  await flush();
  fixture.detectChanges();
  await fixture.whenStable();
  await flush();
  fixture.detectChanges();
}

const html = (fixture: ComponentFixture<unknown>) => fixture.nativeElement as HTMLElement;

function installation(extra: Partial<SparkAppInstallation> = {}): SparkAppInstallation {
  return {
    id: 'in_1', appSlug: 'ci-bot', appName: 'CI bot', targetKind: 'user', targetKey: 'Users/1', permissions: ['reports:read'],
    pendingPermissions: [], events: ['report.completed'], suspended: false, installedBy: 'Users/1', installedUtc: '2026-11-18T10:00:00Z',
    ...extra,
  };
}

const prompt: SparkAppInstallPrompt = {
  app: {
    slug: 'ci-bot', name: 'CI bot', description: 'Builds things', homepageUrl: 'https://ci.example', ownerKind: 'user', ownerKey: 'Users/2',
    permissions: ['reports:read'], events: ['report.completed'],
  },
  targets: [
    { kind: 'user', key: 'Users/1', displayName: 'Ann', installationId: 'in_1' },
    { kind: 'organization', key: 'Organizations/1', displayName: 'Acme', installationId: null },
  ],
};

function configure(api: object) {
  const location = { assign: vi.fn() };
  TestBed.configureTestingModule({
    providers: [
      // eslint-disable-next-line @typescript-eslint/no-deprecated
      provideNoopAnimations(),
      provideRouter([]),
      { provide: SparkAppsService, useValue: api },
      { provide: SparkLanguageService, useValue: { t: (key: string) => key.replace('apps.spa.', '') + '|{0}' } },
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['slug', 'ci-bot']]) } } },
      { provide: SPARK_APPS_LEAVE_SITE, useValue: location.assign },
    ],
  });
  return location;
}

describe('SparkAppInstallComponent', () => {
  afterEach(() => vi.restoreAllMocks());

  it('offers only the targets it is not installed on yet, and says where it is', async () => {
    configure({ installPrompt: vi.fn().mockResolvedValue(ok(prompt)) });
    const fixture = TestBed.createComponent(SparkAppInstallComponent);
    await settle(fixture);

    expect(fixture.componentInstance.target()).toBe('organization|Organizations/1');
    expect(html(fixture).querySelector('.spark-app-install-permissions')!.textContent).toContain('reports:read');
    expect(html(fixture).querySelector('.spark-app-install-already')!.textContent).toContain('Ann');
  });

  it("goes on to the App's setup URL after installing, when it has one", async () => {
    const api = {
      installPrompt: vi.fn().mockResolvedValue(ok(prompt)),
      install: vi.fn().mockResolvedValue(ok({ installation: installation(), redirectUrl: 'https://ci.example/setup?installation_id=in_2&setup_action=install' })),
    };
    const location = configure(api);
    const fixture = TestBed.createComponent(SparkAppInstallComponent);
    await settle(fixture);

    await fixture.componentInstance.install();
    expect(api.install).toHaveBeenCalledWith('ci-bot', prompt.targets[1]);
    expect(location.assign).toHaveBeenCalledWith('https://ci.example/setup?installation_id=in_2&setup_action=install');
  });

  it('else to the installations page, through the router', async () => {
    const api = {
      installPrompt: vi.fn().mockResolvedValue(ok(prompt)),
      install: vi.fn().mockResolvedValue(ok({ installation: installation(), redirectUrl: null })),
    };
    const location = configure(api);
    const fixture = TestBed.createComponent(SparkAppInstallComponent);
    await settle(fixture);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);

    await fixture.componentInstance.install();
    expect(navigate).toHaveBeenCalledWith('/account/installations');
    expect(location.assign).not.toHaveBeenCalled();
  });

  it('a private App of someone else is not found', async () => {
    configure({ installPrompt: vi.fn().mockResolvedValue({ ok: false, status: 404, error: null }) });
    const fixture = TestBed.createComponent(SparkAppInstallComponent);
    await settle(fixture);
    expect(fixture.componentInstance.error()).toBe('install.notFound|{0}');
  });
});

describe('SparkAppInstallationsComponent', () => {
  it('grants what the App asks for since, suspends it and uninstalls it after asking', async () => {
    const api = {
      owners: vi.fn().mockResolvedValue(ok([{ kind: 'user', key: 'Users/1', displayName: 'Ann' }])),
      installations: vi.fn().mockResolvedValue(ok([installation({ pendingPermissions: ['notes:read'] })])),
      acceptPermissions: vi.fn().mockResolvedValue(ok(installation())),
      setSuspended: vi.fn().mockResolvedValue(ok(installation({ suspended: true }))),
      uninstall: vi.fn().mockResolvedValue(ok(null)),
    };
    configure(api);
    const fixture = TestBed.createComponent(SparkAppInstallationsComponent);
    await settle(fixture);
    const page = fixture.componentInstance;

    expect(html(fixture).querySelector('.spark-installation-pending')!.textContent).toContain('notes:read');
    html(fixture).querySelector<HTMLButtonElement>('.spark-installation-accept')!.click();
    await settle(fixture);
    expect(api.acceptPermissions).toHaveBeenCalledWith('in_1');

    await page.suspend(installation(), true);
    expect(api.setSuspended).toHaveBeenCalledWith('in_1', true);
    expect(page.notice()).toBe('installations.suspendedNotice|{0}');

    vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValueOnce(true);
    await page.uninstall(installation());
    expect(api.uninstall).not.toHaveBeenCalled();
    await page.uninstall(installation());
    expect(api.uninstall).toHaveBeenCalledWith('in_1');
    expect(api.installations).toHaveBeenCalledTimes(4);
  });
});
