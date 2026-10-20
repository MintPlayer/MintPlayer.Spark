import { DestroyRef, Injectable, Injector, NgZone, Signal, assertInInjectionContext, effect, inject, signal, untracked } from '@angular/core';
import { BehaviorSubject, Observable, Subscription, isObservable, of } from 'rxjs';
import {
  LIVE_CLOSE_MESSAGE_TOO_LARGE,
  LIVE_CLOSE_REAUTHENTICATE,
  LIVE_CLOSE_TOO_MANY_SOCKETS,
  LiveClientOp,
  LiveErrorEvent,
  LiveFailureCode,
  LivePoEvent,
  LivePoRequest,
  LiveQueryEvent,
  LiveQueryRequest,
  LiveServerFrame,
  LiveTopicEvent,
  PersistentObject,
  QueryResult,
  SparkDeletedFilter,
  applyLivePoPatch,
  applyLiveQueryPatch,
} from '@mintplayer/ng-spark/models';
import { SPARK_CONFIG } from '@mintplayer/ng-spark';

type LiveEvent = LivePoEvent | LiveQueryEvent | LiveTopicEvent;

/** What a watcher of one subscription is told. */
interface Watcher {
  next(event: LiveEvent): void;
  complete(): void;
}

/** A topic subscription's request: the topic's name (the same shape for a broadcast and a private stream). */
interface LiveTopicRequest {
  topic: string;
}

type EntryKind = 'po' | 'query' | 'topic' | 'private';

/** One server subscription, shared by every watcher of the same canonical request. */
interface Entry {
  key: string;
  sub: string;
  kind: EntryKind;
  request: LivePoRequest | LiveQueryRequest | LiveTopicRequest;
  watchers: Set<Watcher>;
  /**
   * The state with every frame applied; null until the first `po`/`result` (and again after a reconnect).
   * For a topic, its latest `topic` event.
   */
  state: PersistentObject | QueryResult | LiveTopicEvent | null;
}

/** Options of {@link SparkLiveService.watchPersistentObject}. */
export interface LivePoWatchOptions {
  /** The soft-deletion mode the object is loaded with, as `/spark/po/load` takes it. */
  deleted?: SparkDeletedFilter | null;
}

/** Options of the signal helpers: an injector when called outside an injection context. */
export interface LiveSignalOptions {
  injector?: Injector;
}

/** Options of {@link SparkLiveService.liveTopic}. */
export interface LiveTopicSignalOptions extends LiveSignalOptions {
  /** Follow this browser's own stream on the topic (`private`) instead of the broadcast. */
  private?: boolean;
}

/** The first reconnect delay, doubled per attempt up to {@link MAX_DELAY_MS}, then jittered to 50–100 %. */
const BASE_DELAY_MS = 1000;
const MAX_DELAY_MS = 30000;
/** A second 4401 this soon after reconnecting for one is not a lifetime rotation: the login is gone. */
const REAUTH_LOOP_MS = 5000;
const OPEN = 1;

/**
 * Live updates over ONE lazy WebSocket to `/spark/live` (X4; `docs/guide-live-updates.md`).
 *
 * - Subscriptions are ref-counted by a canonical JSON of their request, so two components watching the
 *   same object or the same query page share one server subscription; the last one to leave unsubscribes,
 *   and the socket closes when nothing is watched.
 * - `po`/`result` replace the held state and `poPatch`/`patch` are applied to it, so a watcher always
 *   receives the whole object or page.
 * - A subscription's `error` or `gone` ends that subscription only: it is never a reason to reconnect.
 *   (The streaming service it replaces retried ten times on "access denied".)
 * - The socket reconnects with exponential backoff and jitter after a network loss or a server restart,
 *   then resubscribes everything, which yields fresh snapshots. `4401` reconnects at once (the cookie is
 *   validated afresh) unless it repeats within seconds; `4429` and `1009` are surfaced, not retried.
 * - The socket and its timers run outside the Angular zone; only emissions re-enter it.
 * - Topics (commerce PRD X2): a broadcast value, or this browser's own private stream, retained by the
 *   server, so a late joiner and a reconnect get the latest value at once.
 * - Every `hello`, `ping` and `topic` frame carries the server's clock: {@link serverNow} counts down against
 *   the server, not against a visitor's wrong clock.
 */
@Injectable({ providedIn: 'root' })
export class SparkLiveService {
  private readonly config = inject(SPARK_CONFIG, { optional: true });
  private readonly baseUrl = this.config?.baseUrl ?? '/spark';
  private readonly ngZone = inject(NgZone);

  private readonly offset = signal(0);
  /**
   * The server's clock minus this browser's, in milliseconds, measured from the live frames (0 until the first
   * one). Each sample is `serverTime − Date.now()` on arrival, which is short by the frame's transit time, so the
   * largest sample since the socket opened is kept.
   */
  readonly serverTimeOffset = this.offset.asReadonly();
  private offsetMeasured = false;

  private readonly entries = new Map<string, Entry>();
  private readonly bySub = new Map<string, Entry>();
  private nextSub = 0;

  constructor() {
    // Sign-in and sign-out (raised by @mintplayer/ng-spark/auth/core as 'spark-identity-changed'): a socket's
    // identity is fixed when it connects, so private streams follow the new identity only on a fresh socket.
    if (typeof globalThis.addEventListener !== 'function') return;
    const onIdentityChanged = () => this.reconnect();
    globalThis.addEventListener('spark-identity-changed', onIdentityChanged);
    inject(DestroyRef).onDestroy(() => globalThis.removeEventListener('spark-identity-changed', onIdentityChanged));
  }

  private socket: WebSocket | null = null;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private attempt = 0;
  private lastReauthAt = 0;

  /**
   * Follows one persistent object: `po` with the object as `/spark/po/load` returns it (patches applied),
   * then `gone` or `error`, after either of which the stream completes.
   */
  watchPersistentObject(objectTypeId: string, id: string, options?: LivePoWatchOptions): Observable<LivePoEvent> {
    const request: LivePoRequest = { objectTypeId, id, deleted: options?.deleted ?? null };
    return new Observable<LivePoEvent>(subscriber => {
      const watcher: Watcher = {
        next: event => subscriber.next(event as LivePoEvent),
        complete: () => subscriber.complete(),
      };
      const entry = this.attach('po', request, watcher);
      return () => this.detach(entry, watcher);
    });
  }

  /**
   * Follows one query page: `result` with the page as `/spark/queries/execute` returns it (patches applied),
   * or `error`, after which the stream completes.
   *
   * Given an observable, the page follows it: a new request is sent as an `update` of the same server
   * subscription when this watcher has it to itself, and joins (or opens) the subscription of the new request
   * otherwise. `null` leaves the current subscription without completing.
   */
  watchQuery(request: LiveQueryRequest | Observable<LiveQueryRequest | null>): Observable<LiveQueryEvent> {
    const requests = isObservable(request) ? request : of(request);
    return new Observable<LiveQueryEvent>(subscriber => {
      let entry: Entry | null = null;
      let ended = false;
      const watcher: Watcher = {
        next: event => subscriber.next(event as LiveQueryEvent),
        complete: () => {
          ended = true;
          entry = null;
          subscriber.complete();
        },
      };

      const requestSubscription: Subscription = requests.subscribe(next => {
        if (ended) return;
        if (!next) {
          if (entry) this.detach(entry, watcher);
          entry = null;
          return;
        }
        const key = keyOf('query', next);
        if (entry?.key === key) return;
        if (entry && entry.watchers.size === 1 && !this.entries.has(key)) {
          this.update(entry, key, next);
          return;
        }
        if (entry) this.detach(entry, watcher);
        entry = this.attach('query', next, watcher);
      });

      return () => {
        requestSubscription.unsubscribe();
        if (entry) this.detach(entry, watcher);
        entry = null;
      };
    });
  }

  /**
   * A signal of one live object for a custom page: `null` until the first state (and after an `error`),
   * the object afterwards, `'gone'` once it is deleted or no longer readable. Follows `type` and `id`;
   * a null or empty id watches nothing. Call it in an injection context or pass an injector.
   */
  livePersistentObject(type: Signal<string>, id: Signal<string | null | undefined>, options?: LiveSignalOptions): Signal<PersistentObject | null | 'gone'> {
    if (!options?.injector) assertInInjectionContext(this.livePersistentObject);
    const injector = options?.injector ?? inject(Injector);
    const value = signal<PersistentObject | null | 'gone'>(null);
    effect(onCleanup => {
      const objectTypeId = type();
      const objectId = id();
      untracked(() => value.set(null));
      if (!objectTypeId || !objectId) return;
      const subscription = this.watchPersistentObject(objectTypeId, objectId).subscribe(event => {
        if (event.type === 'po') value.set(event.po);
        else if (event.type === 'gone') value.set('gone');
        else value.set(null);
      });
      onCleanup(() => subscription.unsubscribe());
    }, { injector });
    return value.asReadonly();
  }

  /**
   * A signal of one live query page for a custom page: `null` until the first page (and after an `error`).
   * A changed request (page, sort, search) is sent as an `update`; `null` watches nothing. Call it in an
   * injection context or pass an injector.
   */
  liveQuery(request: Signal<LiveQueryRequest | null>, options?: LiveSignalOptions): Signal<QueryResult | null> {
    if (!options?.injector) assertInInjectionContext(this.liveQuery);
    const injector = options?.injector ?? inject(Injector);
    const value = signal<QueryResult | null>(null);
    const requests = new BehaviorSubject<LiveQueryRequest | null>(null);
    const subscription = this.watchQuery(requests).subscribe(event => {
      value.set(event.type === 'result' ? event.result : null);
    });
    effect(() => {
      const next = request();
      untracked(() => requests.next(next));
    }, { injector });
    injector.get(DestroyRef).onDestroy(() => {
      subscription.unsubscribe();
      requests.complete();
    });
    return value.asReadonly();
  }

  /**
   * Follows a broadcast topic (commerce PRD X2): `topic` with its latest value, at once if the server holds one,
   * then on every publish; or `error` (`unavailable` for an unknown or refused topic), after which the stream
   * completes.
   */
  watchTopic<T = unknown>(topic: string): Observable<LiveTopicEvent<T>> {
    return this.watchTopicOf<T>('topic', topic);
  }

  /**
   * Follows this browser's own private stream on `topic` — keyed on the signed-in user, else on the visitor
   * cookie. A browser known only by its address gets `unavailable`. Call {@link reconnect} after signing in or
   * out: a socket's identity is fixed when it connects.
   */
  watchPrivate<T = unknown>(topic: string): Observable<LiveTopicEvent<T>> {
    return this.watchTopicOf<T>('private', topic);
  }

  /**
   * A signal of a topic's latest value for a custom page: `null` until the first value (and after an `error`).
   * Follows `topic`; `null` watches nothing. Call it in an injection context or pass an injector.
   */
  liveTopic<T = unknown>(topic: Signal<string | null>, options?: LiveTopicSignalOptions): Signal<T | null> {
    if (!options?.injector) assertInInjectionContext(this.liveTopic);
    const injector = options?.injector ?? inject(Injector);
    const value = signal<T | null>(null);
    effect(onCleanup => {
      const name = topic();
      untracked(() => value.set(null));
      if (!name) return;
      const source = options?.private ? this.watchPrivate<T>(name) : this.watchTopic<T>(name);
      const subscription = source.subscribe(event => value.set(event.type === 'topic' ? event.data : null));
      onCleanup(() => subscription.unsubscribe());
    }, { injector });
    return value.asReadonly();
  }

  /** The server's clock now, in Unix milliseconds: `Date.now()` corrected by {@link serverTimeOffset}. */
  serverNow(): number {
    return Date.now() + this.offset();
  }

  /**
   * Opens a fresh socket now, resubscribing everything, when one is open. A socket's identity (user, visitor)
   * is fixed when it connects, so call this after signing in or out; private streams then follow the new one.
   */
  reconnect(): void {
    if (!this.socket && !this.reconnectTimer) return;
    this.closeSocket();
    if (this.entries.size === 0) return;
    for (const entry of this.entries.values()) entry.state = null;
    this.open();
  }

  private watchTopicOf<T>(kind: 'topic' | 'private', topic: string): Observable<LiveTopicEvent<T>> {
    const request: LiveTopicRequest = { topic };
    return new Observable<LiveTopicEvent<T>>(subscriber => {
      const watcher: Watcher = {
        next: event => subscriber.next(event as LiveTopicEvent<T>),
        complete: () => subscriber.complete(),
      };
      const entry = this.attach(kind, request, watcher);
      return () => this.detach(entry, watcher);
    });
  }

  // --- subscriptions -------------------------------------------------------------------------------

  private attach(kind: EntryKind, request: LivePoRequest | LiveQueryRequest | LiveTopicRequest, watcher: Watcher): Entry {
    const key = keyOf(kind, request);
    let entry = this.entries.get(key);
    if (entry) {
      entry.watchers.add(watcher);
      // A late joiner starts from the state the others already hold.
      if (entry.state) watcher.next(kind === 'po'
        ? { type: 'po', po: entry.state as PersistentObject }
        : kind === 'query'
          ? { type: 'result', result: entry.state as QueryResult }
          : entry.state as LiveTopicEvent);
      return entry;
    }

    entry = { key, sub: `s${++this.nextSub}`, kind, request: clean(request), watchers: new Set([watcher]), state: null };
    this.entries.set(key, entry);
    this.bySub.set(entry.sub, entry);
    if (this.isOpen()) this.sendSubscribe(entry);
    else this.ensureSocket();
    return entry;
  }

  private update(entry: Entry, key: string, request: LiveQueryRequest): void {
    this.entries.delete(entry.key);
    entry.key = key;
    entry.request = clean(request);
    this.entries.set(key, entry);
    // While the socket is down the resubscribe on open carries the new request.
    if (this.isOpen()) this.send({ op: 'update', sub: entry.sub, query: entry.request as LiveQueryRequest });
  }

  private detach(entry: Entry, watcher: Watcher): void {
    if (!entry.watchers.delete(watcher)) return;
    if (entry.watchers.size > 0 || this.entries.get(entry.key) !== entry) return;
    this.forget(entry);
    if (this.isOpen()) this.send({ op: 'unsubscribe', sub: entry.sub });
    this.closeIfIdle();
  }

  private forget(entry: Entry): void {
    if (this.entries.get(entry.key) === entry) this.entries.delete(entry.key);
    this.bySub.delete(entry.sub);
  }

  /** Ends one subscription for all its watchers: the event, then completion. */
  private end(entry: Entry, event: LiveEvent): void {
    this.forget(entry);
    const watchers = [...entry.watchers];
    entry.watchers.clear();
    this.emit(() => {
      for (const watcher of watchers) {
        watcher.next(event);
        watcher.complete();
      }
    });
  }

  /** Ends every subscription with a socket-level failure and drops the socket without reconnecting. */
  private failAll(code: LiveFailureCode, message: string): void {
    const event: LiveErrorEvent = { type: 'error', code, message };
    for (const entry of [...this.entries.values()]) this.end(entry, event);
    this.closeSocket();
  }

  // --- the socket ----------------------------------------------------------------------------------

  private isOpen(): boolean {
    return this.socket !== null && this.socket.readyState === OPEN;
  }

  private ensureSocket(): void {
    if (this.socket || this.reconnectTimer) return;
    this.open();
  }

  private open(): void {
    this.ngZone.runOutsideAngular(() => {
      const socket = new WebSocket(this.buildWebSocketUrl());
      this.socket = socket;
      socket.onopen = () => {
        if (this.socket !== socket) return;
        for (const entry of this.entries.values()) this.sendSubscribe(entry);
      };
      socket.onmessage = event => {
        if (this.socket !== socket) return;
        this.onFrame(event.data);
      };
      socket.onerror = () => {
        // onclose follows and decides.
      };
      socket.onclose = event => {
        if (this.socket !== socket) return;
        this.socket = null;
        this.onClose(event.code);
      };
    });
  }

  private onClose(code: number): void {
    if (this.entries.size === 0) return;
    // Every subscription starts over on the next socket with a fresh snapshot.
    for (const entry of this.entries.values()) entry.state = null;

    if (code === LIVE_CLOSE_TOO_MANY_SOCKETS) {
      this.failAll('tooManySockets', 'Too many live connections are open for this user.');
      return;
    }
    if (code === LIVE_CLOSE_MESSAGE_TOO_LARGE) {
      this.failAll('messageTooLarge', 'A live request was too large.');
      return;
    }
    if (code === LIVE_CLOSE_REAUTHENTICATE) {
      const now = Date.now();
      if (this.lastReauthAt && now - this.lastReauthAt < REAUTH_LOOP_MS) {
        this.lastReauthAt = 0;
        this.failAll('unauthenticated', 'The live connection was refused: sign in again.');
        return;
      }
      this.lastReauthAt = now;
      this.open();
      return;
    }

    const delay = Math.min(BASE_DELAY_MS * Math.pow(2, this.attempt), MAX_DELAY_MS) * (0.5 + Math.random() / 2);
    this.attempt++;
    this.ngZone.runOutsideAngular(() => {
      this.reconnectTimer = setTimeout(() => {
        this.reconnectTimer = null;
        if (this.entries.size > 0) this.open();
      }, delay);
    });
  }

  /**
   * Closes the socket once nothing is watched — after a microtask, so a watcher that switches objects
   * (leave one, watch the next in the same turn) keeps the socket instead of reconnecting.
   */
  private closeIfIdle(): void {
    if (this.entries.size > 0) return;
    void Promise.resolve().then(() => {
      if (this.entries.size === 0) this.closeSocket();
    });
  }

  private closeSocket(): void {
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    const socket = this.socket;
    this.socket = null;
    this.attempt = 0;
    if (socket && socket.readyState <= OPEN) socket.close(1000, 'No live subscriptions');
  }

  private sendSubscribe(entry: Entry): void {
    switch (entry.kind) {
      case 'po':
        this.send({ op: 'subscribe', sub: entry.sub, po: entry.request as LivePoRequest });
        break;
      case 'query':
        this.send({ op: 'subscribe', sub: entry.sub, query: entry.request as LiveQueryRequest });
        break;
      case 'topic':
        this.send({ op: 'subscribe', sub: entry.sub, topic: (entry.request as LiveTopicRequest).topic });
        break;
      case 'private':
        this.send({ op: 'subscribe', sub: entry.sub, private: (entry.request as LiveTopicRequest).topic });
        break;
    }
  }

  /**
   * A server clock reading. The first one on a socket (its `hello`) starts over; later ones keep the largest
   * sample, because each one is short by the frame's transit time.
   */
  private sampleServerTime(serverTime: unknown, first: boolean): void {
    if (typeof serverTime !== 'number' || !Number.isFinite(serverTime)) return;
    const sample = serverTime - Date.now();
    const next = first || !this.offsetMeasured ? sample : Math.max(this.offset(), sample);
    this.offsetMeasured = true;
    if (next !== this.offset()) this.emit(() => this.offset.set(next));
  }

  private send(op: LiveClientOp): void {
    this.socket?.send(JSON.stringify(op));
  }

  private onFrame(data: unknown): void {
    let frame: LiveServerFrame;
    try {
      frame = JSON.parse(String(data));
    } catch {
      return;
    }
    if (!frame || typeof frame !== 'object') return;
    // The connection is healthy again: the next loss starts the backoff from its first step.
    this.attempt = 0;
    if (frame.type === 'hello') {
      this.sampleServerTime(frame.serverTime, true);
      return;
    }
    if (frame.type === 'ping') {
      this.sampleServerTime(frame.serverTime, false);
      return;
    }
    if (frame.type === 'error' && !frame.sub) {
      console.warn(`[spark] live: ${frame.code}: ${frame.message}`);
      return;
    }

    const entry = this.bySub.get((frame as { sub: string }).sub);
    if (!entry) return;

    switch (frame.type) {
      case 'po':
        entry.state = frame.po;
        this.broadcast(entry, { type: 'po', po: frame.po });
        break;
      case 'poPatch':
        if (!entry.state) return;
        entry.state = applyLivePoPatch(entry.state as PersistentObject, frame.patch);
        this.broadcast(entry, { type: 'po', po: entry.state as PersistentObject });
        break;
      case 'gone':
        this.end(entry, { type: 'gone' });
        this.closeIfIdle();
        break;
      case 'result':
        entry.state = frame.result;
        this.broadcast(entry, { type: 'result', result: frame.result });
        break;
      case 'patch':
        if (!entry.state) return;
        entry.state = applyLiveQueryPatch(entry.state as QueryResult, frame.patch);
        this.broadcast(entry, { type: 'result', result: entry.state as QueryResult });
        break;
      case 'topic': {
        this.sampleServerTime(frame.serverTime, false);
        const event: LiveTopicEvent = { type: 'topic', data: frame.data, seq: frame.seq, serverTime: frame.serverTime };
        entry.state = event;
        this.broadcast(entry, event);
        break;
      }
      case 'error':
        this.end(entry, { type: 'error', code: frame.code, message: frame.message });
        this.closeIfIdle();
        break;
    }
  }

  private broadcast(entry: Entry, event: LiveEvent): void {
    const watchers = [...entry.watchers];
    this.emit(() => { for (const watcher of watchers) watcher.next(event); });
  }

  private emit(fn: () => void): void {
    this.ngZone.run(fn);
  }

  private buildWebSocketUrl(): string {
    const path = `${this.baseUrl}/live`;
    if (this.baseUrl.startsWith('http://')) return path.replace(/^http:\/\//, 'ws://');
    if (this.baseUrl.startsWith('https://')) return path.replace(/^https:\/\//, 'wss://');
    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    return `${protocol}//${window.location.host}${path}`;
  }
}

/** The request without `null`/`undefined` fields — what is sent, and what is keyed. */
function clean<T extends object>(request: T): T {
  return JSON.parse(canonicalJson(request)) as T;
}

function keyOf(kind: EntryKind, request: object): string {
  return `${kind}:${canonicalJson(request)}`;
}

/** JSON with object keys sorted and `null`/`undefined` fields dropped, so equal requests key equal. */
export function canonicalJson(value: unknown): string {
  return JSON.stringify(normalize(value));
}

function normalize(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(normalize);
  if (value && typeof value === 'object') {
    const result: Record<string, unknown> = {};
    for (const key of Object.keys(value).sort()) {
      const field = (value as Record<string, unknown>)[key];
      if (field === null || field === undefined) continue;
      result[key] = normalize(field);
    }
    return result;
  }
  return value;
}
