import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { SwPush } from '@angular/service-worker';
import { BehaviorSubject, Subject, of, throwError } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { SPARK_AUTH_STATE } from '@mintplayer/ng-spark';
import { QueryResult } from '@mintplayer/ng-spark/models';
import { SparkService } from '@mintplayer/ng-spark/services';
import {
  SPARK_MY_NOTIFICATIONS_QUERY_ID,
  SPARK_NOTIFICATION_TYPE_ID,
  SPARK_PUSH_PERMISSIONS,
  SPARK_UNREAD_NOTIFICATIONS_QUERY_ID,
  SparkNotificationPermissions,
} from './spark-notification';
import { SparkNotificationsService } from './spark-notifications.service';

/** A controllable `/spark/live` socket (the real SparkLiveService runs on it). */
class FakeWebSocket {
  static instances: FakeWebSocket[] = [];
  readyState = 0;
  sent: any[] = [];
  onopen: ((ev: Event) => void) | null = null;
  onmessage: ((ev: MessageEvent) => void) | null = null;
  onerror: ((ev: Event) => void) | null = null;
  onclose: ((ev: CloseEvent) => void) | null = null;
  constructor(public url: string) { FakeWebSocket.instances.push(this); }
  send(data: string) { this.sent.push(JSON.parse(data)); }
  close() { this.readyState = 3; }
  open() { this.readyState = 1; this.onopen?.(new Event('open')); }
  frame(frame: object) { this.onmessage?.({ data: JSON.stringify(frame) } as MessageEvent); }
  /** The sub id of the latest subscribe for a query. */
  sub(queryId: string): string {
    return [...this.sent].reverse().find(s => s.op === 'subscribe' && s.query?.queryId === queryId)!.sub;
  }
}

const row = (id: string, title: string, read = false, url?: string) => ({
  id,
  values: [
    { key: 'Title', value: title },
    { key: 'Body', value: `${title} body` },
    { key: 'CreatedAtUtc', value: '2026-10-10T08:00:00Z' },
    { key: 'IsRead', value: read },
    ...(url ? [{ key: 'Url', value: url }] : []),
  ],
});

const page = (items: any[], totalItems = items.length): QueryResult => ({ columns: [], items, totalItems, skip: 0, take: 8 });

describe('SparkNotificationsService', () => {
  let http: { get: ReturnType<typeof vi.fn>; post: ReturnType<typeof vi.fn> };
  let spark: { get: ReturnType<typeof vi.fn> };
  let router: { url: string; navigateByUrl: ReturnType<typeof vi.fn> };

  function setup(options: { swPush?: object; permissions?: SparkNotificationPermissions | null; authState?: ReturnType<typeof signal> } = {}) {
    TestBed.configureTestingModule({
      providers: [
        { provide: HttpClient, useValue: http },
        { provide: SparkService, useValue: spark },
        { provide: Router, useValue: router },
        { provide: SPARK_PUSH_PERMISSIONS, useValue: options.permissions ?? null },
        ...(options.swPush ? [{ provide: SwPush, useValue: options.swPush }] : []),
        ...(options.authState ? [{ provide: SPARK_AUTH_STATE, useValue: options.authState }] : []),
      ],
    });
    const service = TestBed.inject(SparkNotificationsService);
    TestBed.tick();
    return service;
  }

  const socket = () => FakeWebSocket.instances[FakeWebSocket.instances.length - 1];

  beforeEach(() => {
    FakeWebSocket.instances = [];
    vi.stubGlobal('WebSocket', FakeWebSocket);
    http = {
      get: vi.fn(() => of({ publicKey: 'BKEY' })),
      post: vi.fn(() => of({ marked: 2 })),
    };
    spark = { get: vi.fn() };
    router = { url: '/', navigateByUrl: vi.fn().mockResolvedValue(true) };
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.unstubAllGlobals();
  });

  describe('in-app', () => {
    it('follows both live queries over one socket, the unread one a single row', () => {
      setup();
      socket().open();
      expect(FakeWebSocket.instances.length).toBe(1);
      const subscribes = socket().sent.filter(s => s.op === 'subscribe').map(s => s.query);
      expect(subscribes).toEqual(expect.arrayContaining([
        { queryId: SPARK_UNREAD_NOTIFICATIONS_QUERY_ID, skip: 0, take: 1 },
        { queryId: SPARK_MY_NOTIFICATIONS_QUERY_ID, skip: 0, take: 8 },
      ]));
    });

    it('takes the unread count from the result, then from each patch', () => {
      const service = setup();
      socket().open();
      expect(service.unreadCount()).toBe(0);

      const sub = socket().sub(SPARK_UNREAD_NOTIFICATIONS_QUERY_ID);
      socket().frame({ type: 'result', sub, result: page([row('n/1', 'One')], 3) });
      expect(service.unreadCount()).toBe(3);

      socket().frame({ type: 'patch', sub, patch: { upsert: [row('n/2', 'Two')], order: ['n/2'], set: { totalItems: 4 } } });
      expect(service.unreadCount()).toBe(4);

      socket().frame({ type: 'patch', sub, patch: { remove: ['n/2'], set: { totalItems: 0 } } });
      expect(service.unreadCount()).toBe(0);
    });

    it('lists the latest notifications from their live query', () => {
      const service = setup();
      socket().open();
      expect(service.loaded()).toBe(false);
      const sub = socket().sub(SPARK_MY_NOTIFICATIONS_QUERY_ID);
      socket().frame({ type: 'result', sub, result: page([row('n/2', 'Two'), row('n/1', 'One', true)]) });

      expect(service.loaded()).toBe(true);
      expect(service.recent().map(n => [n.id, n.title, n.body, n.isRead])).toEqual([
        ['n/2', 'Two', 'Two body', false],
        ['n/1', 'One', 'One body', true],
      ]);
      expect(service.recent()[0].createdAt?.toISOString()).toBe('2026-10-10T08:00:00.000Z');
    });

    it('counts nothing after a subscription error (signed out)', () => {
      const service = setup();
      socket().open();
      const sub = socket().sub(SPARK_UNREAD_NOTIFICATIONS_QUERY_ID);
      socket().frame({ type: 'result', sub, result: page([], 5) });
      socket().frame({ type: 'error', sub, code: 'forbidden', message: 'no' });
      expect(service.unreadCount()).toBe(0);
    });

    it('subscribes again when the signed-in user changes', () => {
      const authState = signal<unknown>(null);
      const service = setup({ authState });
      socket().open();
      const first = socket().sub(SPARK_UNREAD_NOTIFICATIONS_QUERY_ID);
      socket().frame({ type: 'result', sub: first, result: page([], 2) });

      authState.set({ userName: 'alice' });
      TestBed.tick();

      expect(service.unreadCount()).toBe(0);
      expect(socket().sent.filter(s => s.op === 'unsubscribe').map(s => s.sub)).toContain(first);
      const second = socket().sub(SPARK_UNREAD_NOTIFICATIONS_QUERY_ID);
      expect(second).not.toBe(first);
      socket().frame({ type: 'result', sub: second, result: page([], 7) });
      expect(service.unreadCount()).toBe(7);
    });

    it('marks the given ids read, or all unread without ids', async () => {
      const service = setup();
      await expect(service.markRead(['n/1'])).resolves.toBe(2);
      expect(http.post).toHaveBeenCalledWith('/spark/notifications/read', { ids: ['n/1'] });
      await service.markRead();
      expect(http.post).toHaveBeenLastCalledWith('/spark/notifications/read', {});
    });

    it('opens a notification: loads the link the row lacks, marks it read, navigates in-app', async () => {
      spark.get.mockResolvedValue({ attributes: [{ name: 'Url', value: '/runs/42' }] });
      const service = setup();

      await service.open({ id: 'n/1', title: 'One', body: null, url: null, isRead: false, createdAt: null });

      expect(spark.get).toHaveBeenCalledWith(SPARK_NOTIFICATION_TYPE_ID, 'n/1');
      expect(http.post).toHaveBeenCalledWith('/spark/notifications/read', { ids: ['n/1'] });
      expect(router.navigateByUrl).toHaveBeenCalledWith('/runs/42');
    });

    // A same-origin link the router does not know is a server endpoint (SeoTester's /seotester/report.pdf): the
    // router threw NG04002 and nothing opened (M11). It is loaded as a page instead.
    it('loads a same-origin link the router cannot match as a page', async () => {
      router.navigateByUrl.mockRejectedValue(new Error('NG04002: Cannot match any routes'));
      const service = setup();
      const assign = vi.spyOn(service as unknown as { assign(href: string): void }, 'assign').mockImplementation(() => undefined);

      await service.open({ id: 'n/2', title: 'PDF', body: null, url: '/seotester/report.pdf?run=1', isRead: true, createdAt: null });
      await Promise.resolve();

      expect(router.navigateByUrl).toHaveBeenCalledWith('/seotester/report.pdf?run=1');
      expect(assign).toHaveBeenCalledWith(`${location.origin}/seotester/report.pdf?run=1`);
    });

    it('does not mark a read notification again, nor load a link the row has', async () => {
      const service = setup();
      await service.open({ id: 'n/1', title: 'One', body: null, url: `${location.origin}/runs/7`, isRead: true, createdAt: null });
      expect(spark.get).not.toHaveBeenCalled();
      expect(http.post).not.toHaveBeenCalled();
      expect(router.navigateByUrl).toHaveBeenCalledWith('/runs/7');
    });
  });

  describe('Web Push', () => {
    let subscription: BehaviorSubject<PushSubscription | null>;
    let swPush: { isEnabled: boolean; subscription: BehaviorSubject<PushSubscription | null>; notificationClicks: Subject<{ action: string; notification: NotificationOptions & { title: string } }>; requestSubscription: ReturnType<typeof vi.fn>; unsubscribe: ReturnType<typeof vi.fn> };
    let permissions: { permission: NotificationPermission; requestPermission: ReturnType<typeof vi.fn> };
    const device = { endpoint: 'https://fcm.googleapis.com/fcm/send/abc', toJSON: () => ({ endpoint: 'https://fcm.googleapis.com/fcm/send/abc', keys: { p256dh: 'P', auth: 'A' } }) } as unknown as PushSubscription;

    beforeEach(() => {
      subscription = new BehaviorSubject<PushSubscription | null>(null);
      swPush = {
        isEnabled: true,
        subscription,
        notificationClicks: new Subject(),
        requestSubscription: vi.fn().mockResolvedValue(device),
        unsubscribe: vi.fn().mockResolvedValue(undefined),
      };
      permissions = { permission: 'default', requestPermission: vi.fn().mockResolvedValue('granted') };
    });

    it('is unsupported without the Notification/PushManager APIs (iOS outside an installed app)', () => {
      const service = setup({ swPush, permissions: null });
      expect(service.pushSupported).toBe(false);
      expect(service.pushState()).toBe('unsupported');
    });

    it('is unsupported while the service worker is disabled or absent', () => {
      expect(setup({ swPush: { ...swPush, isEnabled: false }, permissions }).pushState()).toBe('unsupported');
      TestBed.resetTestingModule();
      expect(setup({ permissions }).pushState()).toBe('unsupported');
    });

    it('is denied when the browser blocks notifications for the site', () => {
      permissions.permission = 'denied';
      expect(setup({ swPush, permissions }).pushState()).toBe('denied');
    });

    it('knows whether the server sends Web Push', async () => {
      const service = setup({ swPush, permissions });
      await Promise.resolve();
      expect(service.pushConfigured()).toBe(true);

      TestBed.resetTestingModule();
      http.get.mockReturnValue(throwError(() => new HttpErrorResponse({ status: 404 })));
      const off = setup({ swPush, permissions });
      await Promise.resolve();
      expect(off.pushConfigured()).toBe(false);
    });

    it('enables: asks permission inside the click, subscribes with the server key, registers the device', async () => {
      const service = setup({ swPush, permissions });
      const done = service.enablePush();
      // Before anything was awaited: the prompt must belong to the user gesture.
      expect(permissions.requestPermission).toHaveBeenCalledTimes(1);

      await expect(done).resolves.toBe(true);
      expect(swPush.requestSubscription).toHaveBeenCalledWith({ serverPublicKey: 'BKEY' });
      expect(http.post).toHaveBeenCalledWith('/spark/notifications/push/subscribe', device.toJSON());
      expect(service.pushState()).toBe('on');
    });

    it('does not subscribe when permission is refused', async () => {
      permissions.requestPermission.mockResolvedValue('denied');
      const service = setup({ swPush, permissions });
      await expect(service.enablePush()).resolves.toBe(false);
      expect(swPush.requestSubscription).not.toHaveBeenCalled();
      expect(service.pushState()).toBe('denied');
    });

    it('drops the browser subscription when the server refuses the device', async () => {
      http.post.mockReturnValue(throwError(() => new HttpErrorResponse({ status: 400 })));
      const service = setup({ swPush, permissions });
      await expect(service.enablePush()).resolves.toBe(false);
      expect(swPush.unsubscribe).toHaveBeenCalledTimes(1);
      expect(service.pushState()).toBe('off');
    });

    it('disables: the server forgets the endpoint, then the browser unsubscribes', async () => {
      permissions.permission = 'granted';
      subscription.next(device);
      const service = setup({ swPush, permissions });
      expect(service.pushState()).toBe('on');

      await service.disablePush();

      expect(http.post).toHaveBeenCalledWith('/spark/notifications/push/unsubscribe', { endpoint: device.endpoint });
      expect(swPush.unsubscribe).toHaveBeenCalledTimes(1);
      expect(service.pushState()).toBe('off');
    });

    it('marks a clicked push read (its data.notificationId), and ignores a push without one', async () => {
      setup({ swPush, permissions });

      swPush.notificationClicks.next({ action: '', notification: { title: 'Score dropped', data: { url: '/sites/1', notificationId: 'Notifications/7' } } });
      await Promise.resolve();
      expect(http.post).toHaveBeenCalledWith('/spark/notifications/read', { ids: ['Notifications/7'] });

      http.post.mockClear();
      swPush.notificationClicks.next({ action: '', notification: { title: 'Other', data: { url: '/x' } } });
      await Promise.resolve();
      expect(http.post).not.toHaveBeenCalled();
    });

    it('survives a failed mark-read on a clicked push', async () => {
      http.post.mockReturnValue(throwError(() => new HttpErrorResponse({ status: 500 })));
      setup({ swPush, permissions });
      swPush.notificationClicks.next({ action: '', notification: { title: 'x', data: { notificationId: 'Notifications/8' } } });
      await Promise.resolve();
      expect(http.post).toHaveBeenCalledTimes(1);
    });
  });
});
