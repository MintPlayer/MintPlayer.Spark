import { Component, Injector, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it, beforeEach, afterEach, vi } from 'vitest';
import { BehaviorSubject } from 'rxjs';

import { SparkLiveService } from './spark-live.service';
import { SPARK_CONFIG } from '@mintplayer/ng-spark';
import { LivePoEvent, LiveQueryEvent, LiveQueryRequest, PersistentObject, QueryResult } from '@mintplayer/ng-spark/models';

/**
 * A controllable WebSocket: the test opens it, feeds it server frames and closes it with a code. Every
 * frame the service sends is parsed into `sent`.
 */
class FakeWebSocket {
  static instances: FakeWebSocket[] = [];
  readyState = 0;
  sent: any[] = [];
  closeCalls: { code?: number; reason?: string }[] = [];
  onopen: ((ev: Event) => void) | null = null;
  onmessage: ((ev: MessageEvent) => void) | null = null;
  onerror: ((ev: Event) => void) | null = null;
  onclose: ((ev: CloseEvent) => void) | null = null;

  constructor(public url: string) {
    FakeWebSocket.instances.push(this);
  }

  send(data: string) {
    if (this.readyState !== 1) throw new Error('send on a socket that is not open');
    this.sent.push(JSON.parse(data));
  }

  close(code?: number, reason?: string) {
    this.closeCalls.push({ code, reason });
    this.readyState = 3;
  }

  // --- test drivers ---
  open() {
    this.readyState = 1;
    this.onopen?.(new Event('open'));
  }

  frame(frame: object) {
    this.onmessage?.({ data: JSON.stringify(frame) } as MessageEvent);
  }

  serverClose(code: number) {
    this.readyState = 3;
    this.onclose?.({ code } as CloseEvent);
  }

  ops(op?: string) {
    return op ? this.sent.filter(s => s.op === op) : this.sent;
  }
}

const flush = () => Promise.resolve().then(() => undefined);
const sockets = () => FakeWebSocket.instances;
const lastSocket = () => FakeWebSocket.instances[FakeWebSocket.instances.length - 1];

const po = (etag: string, first = 'Alice'): PersistentObject => ({
  id: 'people/1', name: first, objectTypeId: 'Person', etag,
  attributes: [
    { id: 'a1', name: 'FirstName', value: first } as any,
    { id: 'a2', name: 'LastName', value: 'Smith' } as any,
  ],
} as any);

const row = (id: string, name: string) => ({ id, breadcrumb: name, values: [{ key: 'Name', value: name }] } as any);

const page = (...ids: string[]): QueryResult => ({
  columns: [{ name: 'Name', dataType: 'string', order: 1 } as any],
  items: ids.map(id => row(id, id.toUpperCase())),
  totalItems: ids.length,
  skip: 0,
  take: 20,
});

const request: LiveQueryRequest = { queryId: 'runningaudits', skip: 0, take: 20 };

describe('SparkLiveService', () => {
  let originalWebSocket: typeof WebSocket;

  beforeEach(() => {
    FakeWebSocket.instances = [];
    originalWebSocket = (globalThis as any).WebSocket;
    (globalThis as any).WebSocket = FakeWebSocket;
    vi.useFakeTimers();
    TestBed.resetTestingModule();
  });

  afterEach(() => {
    (globalThis as any).WebSocket = originalWebSocket;
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  function configure(baseUrl?: string) {
    TestBed.configureTestingModule({ providers: baseUrl ? [{ provide: SPARK_CONFIG, useValue: { baseUrl } }] : [] });
    return TestBed.inject(SparkLiveService);
  }

  function collect<T>(source: { subscribe: (o: any) => any }) {
    const events: T[] = [];
    let completed = false;
    const subscription = source.subscribe({ next: (e: T) => events.push(e), complete: () => (completed = true) });
    return { events, subscription, get completed() { return completed; } };
  }

  describe('the socket', () => {
    it('opens nothing until something is watched, then one socket to /spark/live', () => {
      const live = configure('https://api.example.com/spark');
      expect(sockets()).toHaveLength(0);

      collect(live.watchPersistentObject('Person', 'people/1'));
      collect(live.watchQuery(request));

      expect(sockets()).toHaveLength(1);
      expect(lastSocket().url).toBe('wss://api.example.com/spark/live');
    });

    it('derives ws:// from the page for a relative base url', () => {
      const live = configure();
      collect(live.watchQuery(request));
      expect(lastSocket().url).toBe(`ws://${window.location.host}/spark/live`);
    });

    it('subscribes on open, with each request exactly as the HTTP body (nulls dropped)', () => {
      const live = configure();
      collect(live.watchPersistentObject('Person', 'people/1'));
      collect(live.watchQuery({ ...request, search: null }));
      lastSocket().open();

      expect(lastSocket().ops('subscribe')).toEqual([
        { op: 'subscribe', sub: 's1', po: { objectTypeId: 'Person', id: 'people/1' } },
        { op: 'subscribe', sub: 's2', query: { queryId: 'runningaudits', skip: 0, take: 20 } },
      ]);
    });

    it('closes the socket when the last watcher leaves', async () => {
      const live = configure();
      const a = collect(live.watchQuery(request));
      lastSocket().open();

      a.subscription.unsubscribe();

      expect(lastSocket().ops('unsubscribe')).toEqual([{ op: 'unsubscribe', sub: 's1' }]);
      await flush();
      expect(lastSocket().closeCalls).toEqual([{ code: 1000, reason: 'No live subscriptions' }]);
    });
  });

  describe('sharing', () => {
    it('two watchers of the same object share one server subscription; a late joiner gets the held state', () => {
      const live = configure();
      const a = collect<LivePoEvent>(live.watchPersistentObject('Person', 'people/1'));
      lastSocket().open();
      lastSocket().frame({ type: 'po', sub: 's1', po: po('A:1') });

      const b = collect<LivePoEvent>(live.watchPersistentObject('Person', 'people/1'));

      expect(lastSocket().ops('subscribe')).toHaveLength(1);
      expect(b.events).toEqual([{ type: 'po', po: po('A:1') }]);
      lastSocket().frame({ type: 'poPatch', sub: 's1', patch: { set: { etag: 'A:2' } } });
      expect(a.events.at(-1)).toEqual({ type: 'po', po: { ...po('A:1'), etag: 'A:2' } });
      expect(b.events.at(-1)).toEqual(a.events.at(-1));
    });

    it('keys requests canonically: field order and null fields do not split a subscription', () => {
      const live = configure();
      collect(live.watchQuery({ queryId: 'q', skip: 0, take: 20 }));
      collect(live.watchQuery({ take: 20, search: null, skip: 0, queryId: 'q' } as LiveQueryRequest));
      lastSocket().open();

      expect(lastSocket().ops('subscribe')).toHaveLength(1);
    });

    it('unsubscribes only when the LAST watcher of a shared subscription leaves', async () => {
      const live = configure();
      const a = collect(live.watchPersistentObject('Person', 'people/1'));
      const b = collect(live.watchPersistentObject('Person', 'people/1'));
      const other = collect(live.watchPersistentObject('Person', 'people/2'));
      lastSocket().open();

      a.subscription.unsubscribe();
      expect(lastSocket().ops('unsubscribe')).toHaveLength(0);

      b.subscription.unsubscribe();
      expect(lastSocket().ops('unsubscribe')).toEqual([{ op: 'unsubscribe', sub: 's1' }]);
      // people/2 is still watched: the socket stays.
      await flush();
      expect(lastSocket().closeCalls).toHaveLength(0);
      other.subscription.unsubscribe();
      await flush();
      expect(lastSocket().closeCalls).toHaveLength(1);
    });
  });

  describe('applying frames', () => {
    function watchPage() {
      const live = configure();
      const watch = collect<LiveQueryEvent>(live.watchQuery(request));
      lastSocket().open();
      lastSocket().frame({ type: 'result', sub: 's1', result: page('a', 'b', 'c') });
      const ids = () => {
        const last = watch.events.at(-1)!;
        return last.type === 'result' ? last.result.items.map(i => i.id) : null;
      };
      return { live, watch, ids };
    }

    /**
     * Defect 2: an added row must appear. The streaming client mapped a patch over the rows it already
     * held (`items.map(item => patch.updated.find(...))`), so a row id it did not hold was dropped and the
     * grid never showed a new document until a reload.
     */
    it('adds a row the page did not hold, in the order the server sends', () => {
      const { watch, ids } = watchPage();
      lastSocket().frame({ type: 'patch', sub: 's1', patch: { upsert: [row('n', 'New')], order: ['a', 'n', 'b', 'c'], set: { totalItems: 4 } } });

      expect(ids()).toEqual(['a', 'n', 'b', 'c']);
      const last = watch.events.at(-1) as Extract<LiveQueryEvent, { type: 'result' }>;
      expect(last.result.totalItems).toBe(4);
      expect(last.result.items[1]).toEqual(row('n', 'New'));
    });

    it('removes rows without an order: the held order minus the removed', () => {
      const { ids } = watchPage();
      lastSocket().frame({ type: 'patch', sub: 's1', patch: { remove: ['b'], set: { totalItems: 2 } } });
      expect(ids()).toEqual(['a', 'c']);
    });

    it('reorders by `order` and replaces changed rows whole', () => {
      const { watch, ids } = watchPage();
      lastSocket().frame({ type: 'patch', sub: 's1', patch: { upsert: [row('c', 'Changed')], order: ['c', 'a', 'b'] } });

      expect(ids()).toEqual(['c', 'a', 'b']);
      const last = watch.events.at(-1) as Extract<LiveQueryEvent, { type: 'result' }>;
      expect(last.result.items[0].breadcrumb).toBe('Changed');
    });

    it('assigns `set`, a null removing the field', () => {
      const { watch } = watchPage();
      lastSocket().frame({ type: 'patch', sub: 's1', patch: { set: { disabledActions: ['New'] } } });
      lastSocket().frame({ type: 'patch', sub: 's1', patch: { set: { disabledActions: null, skip: 20 } } });

      const last = watch.events.at(-1) as Extract<LiveQueryEvent, { type: 'result' }>;
      expect('disabledActions' in last.result).toBe(false);
      expect(last.result.skip).toBe(20);
    });

    it('a `result` replaces the page', () => {
      const { ids } = watchPage();
      lastSocket().frame({ type: 'result', sub: 's1', result: page('x') });
      expect(ids()).toEqual(['x']);
    });

    it('poPatch assigns `set` and replaces attributes by name', () => {
      const live = configure();
      const watch = collect<LivePoEvent>(live.watchPersistentObject('Person', 'people/1'));
      lastSocket().open();
      lastSocket().frame({ type: 'po', sub: 's1', po: po('A:1') });
      lastSocket().frame({ type: 'poPatch', sub: 's1', patch: {
        set: { etag: 'A:2', breadcrumb: 'Bob Smith' },
        attributes: [{ id: 'a1', name: 'FirstName', value: 'Bob' }],
      } });

      const last = watch.events.at(-1) as Extract<LivePoEvent, { type: 'po' }>;
      expect(last.po.etag).toBe('A:2');
      expect(last.po.breadcrumb).toBe('Bob Smith');
      expect(last.po.attributes.map(a => [a.name, a.value])).toEqual([['FirstName', 'Bob'], ['LastName', 'Smith']]);
    });

    it('`gone` ends the subscription: the event, then completion, and no unsubscribe is owed', async () => {
      const live = configure();
      const watch = collect<LivePoEvent>(live.watchPersistentObject('Person', 'people/1'));
      lastSocket().open();
      lastSocket().frame({ type: 'po', sub: 's1', po: po('A:1') });
      lastSocket().frame({ type: 'gone', sub: 's1' });

      expect(watch.events.at(-1)).toEqual({ type: 'gone' });
      expect(watch.completed).toBe(true);
      watch.subscription.unsubscribe();
      expect(lastSocket().ops('unsubscribe')).toHaveLength(0);
      // Nothing is watched any more.
      await flush();
      expect(lastSocket().closeCalls).toHaveLength(1);
    });

    it('ignores pings and frames of subscriptions it no longer holds', () => {
      const { watch } = watchPage();
      const before = watch.events.length;
      lastSocket().frame({ type: 'ping' });
      lastSocket().frame({ type: 'result', sub: 's99', result: page('z') });
      expect(watch.events).toHaveLength(before);
    });
  });

  describe('errors and reconnects', () => {
    /**
     * Defect 3: "access denied" used to close the per-query socket with 1011, and the streaming client
     * reconnected ten times with backoff for an answer that could not change. A subscription error now ends
     * that subscription and nothing else: no new socket, ever.
     */
    it('a subscription `error` ends that subscription without reconnecting', () => {
      const live = configure();
      const denied = collect<LiveQueryEvent>(live.watchQuery(request));
      const other = collect<LivePoEvent>(live.watchPersistentObject('Person', 'people/1'));
      lastSocket().open();

      lastSocket().frame({ type: 'error', sub: 's1', code: 'unavailable', message: 'This list is not available.' });
      vi.advanceTimersByTime(120_000);

      expect(denied.events).toEqual([{ type: 'error', code: 'unavailable', message: 'This list is not available.' }]);
      expect(denied.completed).toBe(true);
      expect(other.completed).toBe(false);
      expect(sockets()).toHaveLength(1);
      expect(lastSocket().closeCalls).toHaveLength(0);
    });

    it('closes the socket when an error ended the last subscription, and opens none after', async () => {
      const live = configure();
      collect(live.watchQuery(request));
      lastSocket().open();
      lastSocket().frame({ type: 'error', sub: 's1', code: 'limit', message: 'Too many subscriptions' });
      await flush();
      vi.advanceTimersByTime(120_000);

      expect(lastSocket().closeCalls).toHaveLength(1);
      expect(sockets()).toHaveLength(1);
    });

    it('reconnects after a network loss with backoff and jitter, and resubscribes with fresh snapshots', () => {
      vi.spyOn(Math, 'random').mockReturnValue(1); // the full delay: 1 s, then 2 s
      const live = configure();
      const watch = collect<LiveQueryEvent>(live.watchQuery(request));
      lastSocket().open();
      lastSocket().frame({ type: 'result', sub: 's1', result: page('a') });

      lastSocket().serverClose(1006);
      vi.advanceTimersByTime(999);
      expect(sockets()).toHaveLength(1);
      vi.advanceTimersByTime(1);
      expect(sockets()).toHaveLength(2);

      // The second socket fails before opening: the next delay doubles.
      lastSocket().serverClose(1006);
      vi.advanceTimersByTime(1999);
      expect(sockets()).toHaveLength(2);
      vi.advanceTimersByTime(1);
      expect(sockets()).toHaveLength(3);

      lastSocket().open();
      expect(lastSocket().ops('subscribe')).toEqual([{ op: 'subscribe', sub: 's1', query: request }]);
      // A patch before the fresh snapshot has nothing to apply to; the snapshot replaces.
      lastSocket().frame({ type: 'patch', sub: 's1', patch: { remove: ['a'] } });
      lastSocket().frame({ type: 'result', sub: 's1', result: page('a', 'b') });
      const last = watch.events.at(-1) as Extract<LiveQueryEvent, { type: 'result' }>;
      expect(last.result.items.map(i => i.id)).toEqual(['a', 'b']);
      expect(watch.completed).toBe(false);
    });

    it('jitters the delay down to half', () => {
      vi.spyOn(Math, 'random').mockReturnValue(0);
      const live = configure();
      collect(live.watchQuery(request));
      lastSocket().open();
      lastSocket().serverClose(1001);
      vi.advanceTimersByTime(500);
      expect(sockets()).toHaveLength(2);
    });

    it('4401 reconnects at once and resubscribes everything', () => {
      const live = configure();
      collect(live.watchQuery(request));
      collect(live.watchPersistentObject('Person', 'people/1'));
      lastSocket().open();

      lastSocket().serverClose(4401);

      expect(sockets()).toHaveLength(2);
      lastSocket().open();
      expect(lastSocket().ops('subscribe').map(s => s.sub)).toEqual(['s1', 's2']);
    });

    it('a 4401 again within seconds stops and surfaces it', () => {
      const live = configure();
      const watch = collect<LiveQueryEvent>(live.watchQuery(request));
      lastSocket().open();
      lastSocket().serverClose(4401);
      lastSocket().open();
      vi.advanceTimersByTime(1000);
      lastSocket().serverClose(4401);
      vi.advanceTimersByTime(120_000);

      expect(sockets()).toHaveLength(2);
      expect(watch.events.at(-1)).toMatchObject({ type: 'error', code: 'unauthenticated' });
      expect(watch.completed).toBe(true);
    });

    it('4401s far apart (socket lifetime rotation) keep reconnecting', () => {
      const live = configure();
      const watch = collect<LiveQueryEvent>(live.watchQuery(request));
      lastSocket().open();
      lastSocket().serverClose(4401);
      lastSocket().open();
      vi.advanceTimersByTime(30 * 60_000);
      lastSocket().serverClose(4401);

      expect(sockets()).toHaveLength(3);
      expect(watch.completed).toBe(false);
    });

    it('4429 does not reconnect and surfaces an error to every watcher', () => {
      const live = configure();
      const a = collect<LiveQueryEvent>(live.watchQuery(request));
      const b = collect<LivePoEvent>(live.watchPersistentObject('Person', 'people/1'));
      lastSocket().open();
      lastSocket().serverClose(4429);
      vi.advanceTimersByTime(120_000);

      expect(sockets()).toHaveLength(1);
      expect(a.events.at(-1)).toMatchObject({ type: 'error', code: 'tooManySockets' });
      expect(b.events.at(-1)).toMatchObject({ type: 'error', code: 'tooManySockets' });
      expect(a.completed && b.completed).toBe(true);

      // "Until asked": a new watcher opens a new socket.
      collect(live.watchQuery(request));
      expect(sockets()).toHaveLength(2);
    });

    it('1009 is a bug: surfaced, not retried', () => {
      const live = configure();
      const watch = collect<LiveQueryEvent>(live.watchQuery(request));
      lastSocket().open();
      lastSocket().serverClose(1009);
      vi.advanceTimersByTime(120_000);

      expect(sockets()).toHaveLength(1);
      expect(watch.events.at(-1)).toMatchObject({ type: 'error', code: 'messageTooLarge' });
    });

    it('does not reconnect once nothing is watched', () => {
      const live = configure();
      const watch = collect(live.watchQuery(request));
      lastSocket().open();
      lastSocket().serverClose(1006);
      watch.subscription.unsubscribe();
      vi.advanceTimersByTime(120_000);
      expect(sockets()).toHaveLength(1);
    });
  });

  describe('changing a query request', () => {
    it('sends `update` on the same sub when the watcher has the subscription to itself', () => {
      const live = configure();
      const requests = new BehaviorSubject<LiveQueryRequest | null>(request);
      const watch = collect<LiveQueryEvent>(live.watchQuery(requests));
      lastSocket().open();
      lastSocket().frame({ type: 'result', sub: 's1', result: page('a', 'b') });

      requests.next({ ...request, skip: 20 });

      expect(lastSocket().ops('update')).toEqual([{ op: 'update', sub: 's1', query: { ...request, skip: 20 } }]);
      expect(lastSocket().ops('subscribe')).toHaveLength(1);
      lastSocket().frame({ type: 'patch', sub: 's1', patch: { upsert: [row('c', 'C')], remove: ['a', 'b'], order: ['c'], set: { skip: 20 } } });
      const last = watch.events.at(-1) as Extract<LiveQueryEvent, { type: 'result' }>;
      expect(last.result.items.map(i => i.id)).toEqual(['c']);
      expect(last.result.skip).toBe(20);
    });

    it('an unchanged request sends nothing', () => {
      const live = configure();
      const requests = new BehaviorSubject<LiveQueryRequest | null>(request);
      collect(live.watchQuery(requests));
      lastSocket().open();
      requests.next({ ...request });
      expect(lastSocket().ops('update')).toHaveLength(0);
    });

    it('leaves a shared subscription instead of updating it under the other watcher', () => {
      const live = configure();
      const requests = new BehaviorSubject<LiveQueryRequest | null>(request);
      collect(live.watchQuery(requests));
      const other = collect<LiveQueryEvent>(live.watchQuery(request));
      lastSocket().open();

      requests.next({ ...request, skip: 20 });

      expect(lastSocket().ops('update')).toHaveLength(0);
      expect(lastSocket().ops('subscribe').map(s => s.query.skip)).toEqual([0, 20]);
      expect(lastSocket().ops('unsubscribe')).toHaveLength(0);
      expect(other.completed).toBe(false);
    });

    it('a request changed while the socket is down is subscribed as such on open', () => {
      const live = configure();
      const requests = new BehaviorSubject<LiveQueryRequest | null>(request);
      collect(live.watchQuery(requests));
      requests.next({ ...request, search: 'x' });
      lastSocket().open();

      expect(lastSocket().ops()).toEqual([{ op: 'subscribe', sub: 's1', query: { ...request, search: 'x' } }]);
    });
  });

  describe('signal helpers', () => {
    @Component({ template: '' })
    class Host {}

    it('livePersistentObject follows the id and reports gone', async () => {
      const live = configure();
      const fixture = TestBed.createComponent(Host);
      const injector = fixture.componentRef.injector;
      const id = signal<string | null>('people/1');
      const value = live.livePersistentObject(signal('Person'), id, { injector });
      TestBed.tick();

      lastSocket().open();
      lastSocket().frame({ type: 'po', sub: 's1', po: po('A:1') });
      expect((value() as PersistentObject).etag).toBe('A:1');

      id.set('people/2');
      TestBed.tick();
      expect(value()).toBeNull();
      expect(lastSocket().ops('unsubscribe')).toEqual([{ op: 'unsubscribe', sub: 's1' }]);
      expect(lastSocket().ops('subscribe').at(-1)).toEqual({ op: 'subscribe', sub: 's2', po: { objectTypeId: 'Person', id: 'people/2' } });

      lastSocket().frame({ type: 'gone', sub: 's2' });
      expect(value()).toBe('gone');
      fixture.destroy();
    });

    it('liveQuery sends an update when the request signal changes, and leaves on destroy', () => {
      const live = configure();
      const fixture = TestBed.createComponent(Host);
      const req = signal<LiveQueryRequest | null>(request);
      const value = live.liveQuery(req, { injector: fixture.componentRef.injector });
      TestBed.tick();
      lastSocket().open();
      lastSocket().frame({ type: 'result', sub: 's1', result: page('a') });
      expect(value()?.items.map(i => i.id)).toEqual(['a']);

      req.set({ ...request, search: 'b' });
      TestBed.tick();
      expect(lastSocket().ops('update')).toEqual([{ op: 'update', sub: 's1', query: { ...request, search: 'b' } }]);

      fixture.destroy();
      expect(lastSocket().ops('unsubscribe')).toEqual([{ op: 'unsubscribe', sub: 's1' }]);
    });

    it('throws outside an injection context without an injector', () => {
      const live = configure();
      expect(() => live.liveQuery(signal(null))).toThrow();
      expect(TestBed.inject(Injector)).toBeTruthy();
    });
  });
});
