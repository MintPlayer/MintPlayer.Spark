import { HttpClient, provideHttpClient } from '@angular/common/http';
import { throwError } from 'rxjs';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { SparkAppsService, permissionCovers, readCatalog } from './spark-apps.service';

function setup() {
  TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
  return { service: TestBed.inject(SparkAppsService), http: TestBed.inject(HttpTestingController) };
}

describe('SparkAppsService', () => {
  it('reads the events and the permissions from the OpenAPI catalog, sorted by name', () => {
    const catalog = readCatalog({
      webhooks: {
        'report.completed': { post: { summary: 'A report completed.', 'x-spark-permission': 'reports:read' } },
        'note.created': { post: {} },
      },
      'x-spark-permissions': ['notes:write', 'reports:read'],
    });
    expect(catalog.events).toEqual([
      { name: 'note.created', summary: 'note.created', permission: null },
      { name: 'report.completed', summary: 'A report completed.', permission: 'reports:read' },
    ]);
    expect(catalog.permissions).toEqual(['notes:write', 'reports:read']);
    expect(readCatalog({})).toEqual({ events: [], permissions: [] });
  });

  it('a write covers the read of the same resource, and nothing else', () => {
    expect(permissionCovers(['reports:write'], 'reports:read')).toBe(true);
    expect(permissionCovers(['reports:read'], 'reports:write')).toBe(false);
    expect(permissionCovers(['notes:write'], 'reports:read')).toBe(false);
    expect(permissionCovers([], null)).toBe(true);
  });

  it("answers the server's error text instead of throwing, and the status", async () => {
    const { service, http } = setup();
    const pending = service.createEndpoint({ ownerKind: 'user', ownerKey: 'u1', url: 'http://x', events: ['ping'] });
    const request = http.expectOne('/spark/apps/webhooks/endpoints');
    expect(request.request.method).toBe('POST');
    request.flush({ error: 'The URL must be https.' }, { status: 400, statusText: 'Bad Request' });
    expect(await pending).toEqual({ ok: false, status: 400, error: 'The URL must be https.' });

    const missing = service.deleteApp('gone');
    http.expectOne('/spark/apps/apps/delete').flush(null, { status: 404, statusText: 'Not Found' });
    expect(await missing).toEqual({ ok: false, status: 404, error: null });
    http.verify();
  });

  it('passes the owner and the paging cursor as query parameters, and unwraps ids', async () => {
    const { service, http } = setup();
    const list = service.endpoints({ kind: 'organization', key: 'Organizations/1' });
    http.expectOne(r => r.url === '/spark/apps/webhooks/endpoints'
      && r.params.get('ownerKind') === 'organization' && r.params.get('ownerKey') === 'Organizations/1').flush([]);
    expect(await list).toEqual({ ok: true, value: [] });

    const page = service.deliveries('ep_1', 'dl_9');
    http.expectOne(r => r.params.get('endpointId') === 'ep_1' && r.params.get('after') === 'dl_9').flush([]);
    await page;

    const redelivered = service.redeliver('dl_9');
    http.expectOne('/spark/apps/webhooks/deliveries/redeliver').flush({ id: 'dl_10' });
    expect(await redelivered).toEqual({ ok: true, value: 'dl_10' });

    const suspended = service.setSuspended('in_1', true);
    const suspend = http.expectOne('/spark/apps/installations/suspend');
    expect(suspend.request.body).toEqual({ id: 'in_1' });
    suspend.flush({});
    await suspended;
    http.verify();
  });

  it('calls every endpoint with its method, its body and its parameters', async () => {
    const { service, http } = setup();
    const owner = { kind: 'user', key: 'u1' };
    const cases: { call: () => Promise<unknown>; method: string; url: string; body?: unknown; params?: Record<string, string> }[] = [
      { call: () => service.owners(), method: 'GET', url: '/spark/apps/webhooks/owners' },
      { call: () => service.updateEndpoint({ id: 'ep_1' } as never), method: 'POST', url: '/spark/apps/webhooks/endpoints/update', body: { id: 'ep_1' } },
      { call: () => service.deleteEndpoint('ep_1'), method: 'POST', url: '/spark/apps/webhooks/endpoints/delete', body: { id: 'ep_1' } },
      { call: () => service.rotateSecret('ep_1'), method: 'POST', url: '/spark/apps/webhooks/endpoints/rotate-secret', body: { id: 'ep_1' } },
      { call: () => service.enableEndpoint('ep_1'), method: 'POST', url: '/spark/apps/webhooks/endpoints/enable', body: { id: 'ep_1' } },
      { call: () => service.apps(owner), method: 'GET', url: '/spark/apps/apps', params: { ownerKind: 'user', ownerKey: 'u1' } },
      { call: () => service.app('bot'), method: 'GET', url: '/spark/apps/apps/app', params: { slug: 'bot' } },
      { call: () => service.createApp({ name: 'Bot' } as never), method: 'POST', url: '/spark/apps/apps', body: { name: 'Bot' } },
      { call: () => service.updateApp({ slug: 'bot' } as never), method: 'POST', url: '/spark/apps/apps/update', body: { slug: 'bot' } },
      { call: () => service.generateKey('bot'), method: 'POST', url: '/spark/apps/apps/keys/generate', body: { slug: 'bot' } },
      { call: () => service.deleteKey('bot', 'k1'), method: 'POST', url: '/spark/apps/apps/keys/delete', body: { slug: 'bot', keyId: 'k1' } },
      { call: () => service.installPrompt('bot'), method: 'GET', url: '/spark/apps/installations/new', params: { slug: 'bot' } },
      { call: () => service.install('bot', owner), method: 'POST', url: '/spark/apps/installations', body: { slug: 'bot', targetKind: 'user', targetKey: 'u1' } },
      { call: () => service.installations(owner), method: 'GET', url: '/spark/apps/installations', params: { targetKind: 'user', targetKey: 'u1' } },
      { call: () => service.appInstallations('bot'), method: 'GET', url: '/spark/apps/apps/installations', params: { slug: 'bot' } },
      { call: () => service.setSuspended('in_1', false), method: 'POST', url: '/spark/apps/installations/unsuspend', body: { id: 'in_1' } },
      { call: () => service.acceptPermissions('in_1'), method: 'POST', url: '/spark/apps/installations/accept', body: { id: 'in_1' } },
      { call: () => service.uninstall('in_1'), method: 'POST', url: '/spark/apps/installations/delete', body: { id: 'in_1' } },
    ];
    for (const c of cases) {
      const pending = c.call();
      const request = http.expectOne(r => r.url === c.url);
      expect(request.request.method).toBe(c.method);
      if (c.body !== undefined) expect(request.request.body).toEqual(c.body);
      for (const [name, value] of Object.entries(c.params ?? {})) expect(request.request.params.get(name)).toBe(value);
      request.flush({ answer: c.url });
      expect(await pending).toEqual({ ok: true, value: { answer: c.url } });
    }
    http.verify();
  });

  it('reads the catalog and unwraps the ping id, and passes a failure through', async () => {
    const { service, http } = setup();
    const catalog = service.catalog();
    http.expectOne('/spark/apps/webhooks/catalog').flush({ webhooks: { 'a.b': { post: {} } }, 'x-spark-permissions': ['a:read'] });
    expect(await catalog).toEqual({ ok: true, value: { events: [{ name: 'a.b', summary: 'a.b', permission: null }], permissions: ['a:read'] } });

    const failed = service.catalog();
    http.expectOne('/spark/apps/webhooks/catalog').flush('down', { status: 503, statusText: 'Unavailable' });
    expect(await failed).toEqual({ ok: false, status: 503, error: null });

    const ping = service.ping('ep_1');
    const request = http.expectOne('/spark/apps/webhooks/endpoints/ping');
    expect(request.request.body).toEqual({ id: 'ep_1' });
    request.flush({ id: 'dl_1' });
    expect(await ping).toEqual({ ok: true, value: 'dl_1' });

    const page = service.deliveries('ep_1');
    const first = http.expectOne(r => r.url === '/spark/apps/webhooks/deliveries');
    expect(first.request.params.has('after')).toBe(false);
    first.flush([]);
    await page;
    http.verify();
  });

  it('answers status 0 for an error that is not an HTTP answer', async () => {
    TestBed.configureTestingModule({ providers: [{ provide: HttpClient, useValue: { get: () => throwError(() => new Error('offline')) } }] });
    const service = TestBed.inject(SparkAppsService);
    expect(await service.owners()).toEqual({ ok: false, status: 0, error: null });
  });
});
