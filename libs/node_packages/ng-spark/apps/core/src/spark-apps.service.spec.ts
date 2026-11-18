import { provideHttpClient } from '@angular/common/http';
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
});
