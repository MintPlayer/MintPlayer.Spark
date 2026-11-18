import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
// eslint-disable-next-line @typescript-eslint/no-deprecated -- ng-bootstrap components emit synthetic animation props
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SparkAppsService, type SparkWebhookDelivery } from '@mintplayer/ng-spark/apps/core';
import { SparkWebhookDeliveriesComponent } from './spark-webhook-deliveries.component';

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

function delivery(n: number, extra: Partial<SparkWebhookDelivery> = {}): SparkWebhookDelivery {
  return {
    id: `ep_1/${n}`, messageId: `msg_${n}`, eventType: 'order.shipped', status: 'Succeeded', createdUtc: '2026-11-18T10:00:00Z', completedUtc: null,
    attempts: [], redeliveryOf: null, payload: '{"type":"order.shipped"}', ...extra,
  };
}

function render(api: object) {
  TestBed.configureTestingModule({
    providers: [
      // eslint-disable-next-line @typescript-eslint/no-deprecated
      provideNoopAnimations(),
      provideRouter([]),
      { provide: SparkAppsService, useValue: api },
      { provide: SparkLanguageService, useValue: { t: (key: string) => key.replace('apps.spa.', '') } },
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['id', 'ep_1']]) } } },
    ],
  });
  return TestBed.createComponent(SparkWebhookDeliveriesComponent);
}

describe('SparkWebhookDeliveriesComponent', () => {
  afterEach(() => vi.restoreAllMocks());

  it('offers older deliveries only after a full page, and appends them after the last one', async () => {
    const page = Array.from({ length: 50 }, (_, i) => delivery(i));
    const deliveries = vi.fn().mockResolvedValueOnce(ok(page)).mockResolvedValueOnce(ok([delivery(50)]));
    const fixture = render({ deliveries });
    await settle(fixture);

    expect(deliveries).toHaveBeenCalledWith('ep_1', null);
    expect(html(fixture).querySelector('.spark-deliveries-more')).not.toBeNull();
    await fixture.componentInstance.loadMore();
    await settle(fixture);
    expect(deliveries).toHaveBeenLastCalledWith('ep_1', 'ep_1/49');
    expect(fixture.componentInstance.deliveries()).toHaveLength(51);
    expect(html(fixture).querySelector('.spark-deliveries-more')).toBeNull();
  });

  it('opens a delivery with its payload indented, and says when there are none', async () => {
    const fixture = render({ deliveries: vi.fn().mockResolvedValue(ok([delivery(1, { payload: 'not json', attempts: [{ atUtc: '2026-11-18T10:00:01Z', statusCode: 500, error: null, durationMs: 12, responseBody: 'oops' }] }), delivery(2)])) });
    await settle(fixture);

    fixture.componentInstance.toggle('ep_1/2');
    fixture.detectChanges();
    expect(html(fixture).querySelector('.spark-delivery-payload')!.textContent).toBe(JSON.stringify({ type: 'order.shipped' }, null, 2));
    fixture.componentInstance.toggle('ep_1/1');
    fixture.detectChanges();
    expect(html(fixture).querySelector('.spark-delivery-payload')!.textContent).toBe('not json');
    expect(html(fixture).querySelector('.spark-delivery-attempt')!.textContent).toContain('500');
    fixture.componentInstance.toggle('ep_1/1');
    expect(fixture.componentInstance.open()).toBeNull();

    TestBed.resetTestingModule();
    const empty = render({ deliveries: vi.fn().mockResolvedValue(ok([])) });
    await settle(empty);
    expect(html(empty).querySelector('.spark-deliveries-empty')).not.toBeNull();
  });

  it('says not found for a webhook that is not the caller\'s, and failed otherwise', async () => {
    const notFound = render({ deliveries: vi.fn().mockResolvedValue(refused(404)) });
    await settle(notFound);
    expect(notFound.componentInstance.error()).toBe('deliveries.notFound');

    TestBed.resetTestingModule();
    const failed = render({ deliveries: vi.fn().mockResolvedValue(refused(500)) });
    await settle(failed);
    expect(failed.componentInstance.error()).toBe('loadFailed');
  });

  it('redelivers and reloads the log, and shows the refusal of a disabled webhook', async () => {
    const deliveries = vi.fn().mockResolvedValue(ok([delivery(1)]));
    const redeliver = vi.fn()
      .mockResolvedValueOnce(ok({ id: 'ep_1/2' }))
      .mockResolvedValueOnce(refused(400, 'The endpoint is disabled; enable it first.'));
    const fixture = render({ deliveries, redeliver });
    await settle(fixture);
    fixture.componentInstance.toggle('ep_1/1');

    await fixture.componentInstance.redeliver(delivery(1));
    expect(redeliver).toHaveBeenCalledWith('ep_1/1');
    expect(fixture.componentInstance.notice()).toBe('deliveries.redelivered');
    expect(fixture.componentInstance.open()).toBeNull();
    expect(deliveries).toHaveBeenCalledTimes(2);

    await fixture.componentInstance.redeliver(delivery(1));
    expect(fixture.componentInstance.error()).toBe('The endpoint is disabled; enable it first.');
    expect(fixture.componentInstance.busy()).toBe(false);
  });
});
