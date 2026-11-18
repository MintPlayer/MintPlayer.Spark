import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
// eslint-disable-next-line @typescript-eslint/no-deprecated -- bs-alert emits synthetic animation props
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { SparkSecretDialogService } from '@mintplayer/ng-spark/client-operations';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SparkAppsService, type SparkWebhookDelivery, type SparkWebhookEndpoint } from '@mintplayer/ng-spark/apps/core';
import { SparkWebhooksComponent } from './spark-webhooks.component';
import { SparkWebhookDeliveriesComponent } from './spark-webhook-deliveries.component';

const flush = () => new Promise<void>(resolve => setTimeout(resolve, 0));
const ok = <T>(value: T) => ({ ok: true as const, value });
const failed = (status: number, error: string | null = null) => ({ ok: false as const, status, error });

const user = { kind: 'user', key: 'Users/1', displayName: 'Ann' };
const org = { kind: 'organization', key: 'Organizations/1', displayName: 'Acme' };

function endpoint(extra: Partial<SparkWebhookEndpoint> = {}): SparkWebhookEndpoint {
  return {
    id: 'ep_1', ownerKind: 'user', ownerKey: 'Users/1', url: 'https://hooks.example/a', description: null, events: ['note.created'],
    enabled: true, disabledReason: null, createdUtc: '2026-11-18T10:00:00Z', lastSuccessUtc: null, failingSinceUtc: null,
    previousSecretExpiresUtc: null, ...extra,
  };
}

function delivery(id: string): SparkWebhookDelivery {
  return {
    id, messageId: 'msg_' + id, eventType: 'note.created', status: 'Failed', createdUtc: '2026-11-18T10:00:00Z', completedUtc: null,
    attempts: [{ atUtc: '2026-11-18T10:00:01Z', statusCode: 500, error: null, durationMs: 12, responseBody: 'boom' }],
    redeliveryOf: null, payload: '{"type":"note.created","data":{"id":"OgNotes/1"}}',
  };
}

async function settle(fixture: ComponentFixture<unknown>) {
  await flush();
  fixture.detectChanges();
  await fixture.whenStable();
  await flush();
  fixture.detectChanges();
}

function configure(service: object, extra: unknown[] = []) {
  const secrets = { show: vi.fn() };
  TestBed.configureTestingModule({
    providers: [
      // eslint-disable-next-line @typescript-eslint/no-deprecated
      provideNoopAnimations(),
      provideRouter([]),
      { provide: SparkAppsService, useValue: service },
      { provide: SparkSecretDialogService, useValue: secrets },
      { provide: SparkLanguageService, useValue: { t: (key: string) => key.replace('apps.spa.', '') + '|{0}' } },
      ...extra,
    ],
  });
  return secrets;
}

const html = (fixture: ComponentFixture<unknown>) => fixture.nativeElement as HTMLElement;

describe('SparkWebhooksComponent', () => {
  afterEach(() => vi.restoreAllMocks());

  function service<T extends object = object>(extra: T = {} as T) {
    return {
      owners: vi.fn().mockResolvedValue(ok([user, org])),
      catalog: vi.fn().mockResolvedValue(ok({ events: [{ name: 'note.created', summary: 'A note.', permission: null }], permissions: [] })),
      endpoints: vi.fn().mockResolvedValue(ok([endpoint()])),
      ...extra,
    };
  }

  it("lists the first owner's webhooks, and another owner's after a switch", async () => {
    const api = service();
    configure(api);
    const fixture = TestBed.createComponent(SparkWebhooksComponent);
    await settle(fixture);

    expect(api.endpoints).toHaveBeenCalledWith(user);
    expect(html(fixture).querySelectorAll('.spark-webhook').length).toBe(1);
    expect(html(fixture).querySelector('.spark-webhook-deliveries')!.getAttribute('href')).toBe('/account/webhooks/ep_1');

    api.endpoints.mockResolvedValue(ok([]));
    await fixture.componentInstance.choose(org);
    await settle(fixture);
    expect(api.endpoints).toHaveBeenLastCalledWith(org);
    expect(html(fixture).querySelector('.spark-webhooks-empty')).not.toBeNull();
  });

  it('a new webhook shows its secret once, then the list is read again', async () => {
    const api = service({ createEndpoint: vi.fn().mockResolvedValue(ok({ endpoint: endpoint(), secret: 'whsec_abc' })) });
    const secrets = configure(api);
    const fixture = TestBed.createComponent(SparkWebhooksComponent);
    await settle(fixture);
    const page = fixture.componentInstance;

    page.patch({ url: 'https://hooks.example/b', description: '  ' });
    page.toggle('note.created', true);
    page.toggle('note.created', true);
    await page.save();

    expect(api.createEndpoint).toHaveBeenCalledWith({
      ownerKind: 'user', ownerKey: 'Users/1', url: 'https://hooks.example/b', description: null, events: ['note.created'],
    });
    expect(secrets.show).toHaveBeenCalledWith(expect.objectContaining({ value: 'whsec_abc' }));
    expect(page.draft()).toEqual({ id: null, url: '', description: '', events: [] });
    expect(api.endpoints).toHaveBeenCalledTimes(2);
  });

  it("shows the server's refusal, and asks before rotating or deleting", async () => {
    const api = service({
      updateEndpoint: vi.fn().mockResolvedValue(failed(400, 'The URL must be https.')),
      rotateSecret: vi.fn().mockResolvedValue(ok({ endpoint: endpoint(), secret: 'whsec_new' })),
      deleteEndpoint: vi.fn().mockResolvedValue(ok(null)),
    });
    const secrets = configure(api);
    const fixture = TestBed.createComponent(SparkWebhooksComponent);
    await settle(fixture);
    const page = fixture.componentInstance;

    page.edit(endpoint());
    page.patch({ url: 'http://insecure' });
    await page.save();
    expect(api.updateEndpoint).toHaveBeenCalledWith({ id: 'ep_1', url: 'http://insecure', description: null, events: ['note.created'] });
    expect(page.error()).toBe('The URL must be https.');

    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false);
    await page.rotate(endpoint());
    await page.remove(endpoint());
    expect(api.rotateSecret).not.toHaveBeenCalled();
    expect(api.deleteEndpoint).not.toHaveBeenCalled();

    confirm.mockReturnValue(true);
    await page.rotate(endpoint());
    expect(secrets.show).toHaveBeenCalledWith(expect.objectContaining({ value: 'whsec_new', message: 'webhooks.rotatedMessage|{0}' }));
    await page.remove(endpoint());
    expect(api.deleteEndpoint).toHaveBeenCalledWith('ep_1');
    expect(page.notice()).toBe('webhooks.deleted|{0}');
  });

  it('a disabled webhook says why, and offers to enable it again', async () => {
    const api = service({
      endpoints: vi.fn().mockResolvedValue(ok([endpoint({ enabled: false, disabledReason: 'Failing for 5 days.' })])),
      enableEndpoint: vi.fn().mockResolvedValue(ok(endpoint())),
    });
    configure(api);
    const fixture = TestBed.createComponent(SparkWebhooksComponent);
    await settle(fixture);

    expect(html(fixture).querySelector('.spark-webhook-state')!.getAttribute('data-state')).toBe('disabled');
    expect(html(fixture).textContent).toContain('Failing for 5 days.');
    html(fixture).querySelector<HTMLButtonElement>('.spark-webhook-enable')!.click();
    await settle(fixture);
    expect(api.enableEndpoint).toHaveBeenCalledWith('ep_1');
  });
});

describe('SparkWebhookDeliveriesComponent', () => {
  function render(api: object) {
    configure(api, [{ provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['id', 'ep_1']]) } } }]);
    return TestBed.createComponent(SparkWebhookDeliveriesComponent);
  }

  it('pages back through a full page of 50, and stops at a shorter one', async () => {
    const first = Array.from({ length: 50 }, (_, i) => delivery('dl_' + i));
    const api = { deliveries: vi.fn().mockResolvedValueOnce(ok(first)).mockResolvedValueOnce(ok([delivery('dl_50')])) };
    const fixture = render(api);
    await settle(fixture);

    expect(api.deliveries).toHaveBeenCalledWith('ep_1', null);
    expect(html(fixture).querySelector('.spark-deliveries-more')).not.toBeNull();
    await fixture.componentInstance.loadMore();
    await settle(fixture);
    expect(api.deliveries).toHaveBeenLastCalledWith('ep_1', 'dl_49');
    expect(fixture.componentInstance.deliveries().length).toBe(51);
    expect(html(fixture).querySelector('.spark-deliveries-more')).toBeNull();
  });

  it('opens a delivery with its indented payload and attempts, and redelivers it', async () => {
    const api = {
      deliveries: vi.fn().mockResolvedValue(ok([delivery('dl_1')])),
      redeliver: vi.fn().mockResolvedValue(ok('dl_2')),
    };
    const fixture = render(api);
    await settle(fixture);

    html(fixture).querySelector<HTMLButtonElement>('.spark-delivery-toggle')!.click();
    await settle(fixture);
    expect(html(fixture).querySelector('.spark-delivery-payload')!.textContent).toContain('"id": "OgNotes/1"');
    expect(html(fixture).querySelector('.spark-delivery-attempt')!.textContent).toContain('500');

    html(fixture).querySelector<HTMLButtonElement>('.spark-delivery-redeliver')!.click();
    await settle(fixture);
    expect(api.redeliver).toHaveBeenCalledWith('dl_1');
    expect(api.deliveries).toHaveBeenCalledTimes(2);
    expect(fixture.componentInstance.notice()).toBe('deliveries.redelivered|{0}');
  });

  it("says so when the webhook is not the user's", async () => {
    const fixture = render({ deliveries: vi.fn().mockResolvedValue(failed(404)) });
    await settle(fixture);
    expect(fixture.componentInstance.error()).toBe('deliveries.notFound|{0}');
  });
});
