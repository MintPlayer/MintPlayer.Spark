import { DOCUMENT, Location, isPlatformBrowser } from '@angular/common';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { DestroyRef, Injectable, PLATFORM_ID, Signal, WritableSignal, computed, effect, inject, signal, untracked } from '@angular/core';
import { Router } from '@angular/router';
import { SwPush } from '@angular/service-worker';
import { Observable, Subject, Subscription, firstValueFrom, map, merge } from 'rxjs';
import { SPARK_AUTH_STATE, SPARK_CONFIG, sparkSignedIn } from '@mintplayer/ng-spark';
import { LiveQueryRequest, QueryResult } from '@mintplayer/ng-spark/models';
import { SparkLiveService, SparkService, SparkVisitorPresence } from '@mintplayer/ng-spark/services';
import { SparkSessionNotifications } from '@mintplayer/ng-spark/client-operations';
import { sparkInAppUrl } from '@mintplayer/ng-spark/pwa';
import {
  SPARK_MY_NOTIFICATIONS_QUERY_ID,
  SPARK_NOTIFICATIONS_OPTIONS,
  SPARK_NOTIFICATION_TYPE_ID,
  SPARK_PUSH_PERMISSIONS,
  SPARK_UNREAD_NOTIFICATIONS_QUERY_ID,
  SparkBellEntry,
  SparkNotification,
  serverBellEntry,
  sessionBellEntry,
  toSparkNotification,
} from './spark-notification';

/**
 * Web Push on this device: `unsupported` (no service worker, no PushManager or no Notification API:
 * an iPhone/iPad needs the app on its Home Screen), `denied` (blocked in the browser's settings),
 * `off` or `on`.
 */
export type SparkPushState = 'unsupported' | 'denied' | 'off' | 'on';

/**
 * The client of `MintPlayer.Spark.Notifications` (`docs/seo_analyzer_PRD.md` X10, D20, D30).
 *
 * - **In-app**: {@link unreadCount} and {@link recent} follow the live queries `UnreadNotifications`
 *   (one row, for its `totalItems`) and `MyNotifications` (the latest few) over the ONE `/spark/live`
 *   socket of {@link SparkLiveService}. Nothing polls and there is no second channel: a new, read or
 *   deleted notification arrives as a patch. The subscriptions start again whenever `SPARK_AUTH_STATE`
 *   changes (sign-in, sign-out), so a page opened before signing in still counts afterwards.
 * - **Web Push**: {@link enablePush} / {@link disablePush} subscribe this device through Angular's
 *   `SwPush` and register it at `/spark/notifications/push/*`. It needs `withServiceWorker()`
 *   (or `provideServiceWorker`) with the worker enabled; without it {@link pushState} is `unsupported`.
 */
@Injectable({ providedIn: 'root' })
export class SparkNotificationsService {
  private readonly http = inject(HttpClient);
  private readonly live = inject(SparkLiveService);
  private readonly spark = inject(SparkService);
  private readonly router = inject(Router, { optional: true });
  private readonly location = inject(Location, { optional: true });
  private readonly document = inject(DOCUMENT);
  private readonly swPush = inject(SwPush, { optional: true });
  private readonly permissions = inject(SPARK_PUSH_PERMISSIONS);
  private readonly options = inject(SPARK_NOTIFICATIONS_OPTIONS);
  private readonly baseUrl = `${inject(SPARK_CONFIG, { optional: true })?.baseUrl ?? '/spark'}/notifications`;
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));

  private readonly presence = inject(SparkVisitorPresence);
  private readonly authState = inject(SPARK_AUTH_STATE, { optional: true });
  private readonly unread = signal<QueryResult | null>(null);
  private readonly latest = signal<QueryResult | null>(null);

  /** This session's client-operation notifications (`notify`), listed beside the stored ones. */
  readonly session = inject(SparkSessionNotifications);
  /** Whether the bell shows stored notifications at all ({@link SparkNotificationsOptions.server}). */
  readonly server: boolean = this.options.server;
  /**
   * Whether the stored notifications are followed: the server package is there, and the caller is signed in or is an
   * identified visitor (the `spark.visitor.present` cookie, #710 G7). An anonymous caller without a visitor id has none.
   */
  readonly following: Signal<boolean> = computed(() => this.server && (sparkSignedIn(this.authState?.()) || this.presence.present()));

  /** Unread stored notifications of the caller; 0 until the first result and while not {@link following}. */
  readonly serverUnreadCount: Signal<number> = computed(() => this.unread()?.totalItems ?? 0);
  /** Everything the bell counts: unread stored notifications plus unread session ones. */
  readonly unreadCount: Signal<number> = computed(() => this.serverUnreadCount() + this.session.unreadCount());
  /** The latest notifications (read or not), newest first; empty until the first result. */
  readonly recent: Signal<SparkNotification[]> = computed(() => (this.latest()?.items ?? []).map(toSparkNotification));
  /** Whether {@link recent} holds a result yet. */
  readonly loaded: Signal<boolean> = computed(() => this.latest() !== null);

  /** What the bell lists: the recent stored notifications and this session's, newest first. */
  readonly entries: Signal<SparkBellEntry[]> = computed(() => [
    ...this.recent().map(serverBellEntry),
    ...this.session.entries().map(sessionBellEntry),
  ].sort((a, b) => (b.createdAt?.getTime() ?? 0) - (a.createdAt?.getTime() ?? 0)));

  private readonly arrivals = new Subject<SparkBellEntry>();
  /**
   * Each entry as it arrives, for the pop-out: a session notification as it is added, and a stored one that appears
   * unread in the live list after its first result. What the first result already held is not an arrival, so a
   * page load pops nothing out.
   */
  readonly arrived: Observable<SparkBellEntry> = merge(this.arrivals, this.session.arrived.pipe(map(sessionBellEntry)));
  private known: Set<string> | null = null;

  // --- Web Push ----------------------------------------------------------------------------------

  /** Whether this browser can receive Web Push here at all (see {@link SparkPushState}). */
  readonly pushSupported: boolean = this.browser && !!this.swPush?.isEnabled && this.permissions !== null;

  private readonly permission = signal<NotificationPermission>(this.permissions?.permission ?? 'default');
  private readonly subscribed = signal(false);
  private readonly configured = signal<boolean | null>(null);
  private readonly busy = signal(false);
  private publicKey: Promise<string | null> | null = null;

  /** Web Push on this device. */
  readonly pushState: Signal<SparkPushState> = computed(() => {
    if (!this.pushSupported) return 'unsupported';
    if (this.permission() === 'denied') return 'denied';
    return this.subscribed() && this.permission() === 'granted' ? 'on' : 'off';
  });
  /**
   * Whether the server sends Web Push (`GET /spark/notifications/push/key` answered a key): `null` while
   * unknown, `false` when Web Push is not configured or nobody is signed in. Offer no push UI unless `true`.
   */
  readonly pushConfigured: Signal<boolean | null> = this.configured.asReadonly();
  /** True while {@link enablePush} or {@link disablePush} runs. */
  readonly pushBusy: Signal<boolean> = this.busy.asReadonly();

  constructor() {
    if (!this.browser || !this.server) return;

    effect(onCleanup => {
      // Tracked for change: a sign-in, a sign-out or a new visitor starts the subscriptions again.
      const user = this.authState?.();
      if (!this.following()) {
        this.configured.set(false);
        return;
      }
      untracked(() => {
        const subscriptions = new Subscription();
        subscriptions.add(this.follow({ queryId: SPARK_UNREAD_NOTIFICATIONS_QUERY_ID, skip: 0, take: 1 }, this.unread));
        subscriptions.add(this.follow({ queryId: SPARK_MY_NOTIFICATIONS_QUERY_ID, skip: 0, take: this.options.recentCount }, this.latest));
        // Whether push is configured is asked again per user. Push needs an account: a visitor is never asked.
        this.publicKey = null;
        if (sparkSignedIn(user)) void this.loadPublicKey();
        else this.configured.set(false);
        onCleanup(() => {
          subscriptions.unsubscribe();
          this.unread.set(null);
          this.latest.set(null);
          // Another identity's first result is not an arrival either.
          this.known = null;
        });
      });
    });

    if (this.swPush?.isEnabled) {
      const subscription = this.swPush.subscription.subscribe(current => this.subscribed.set(!!current));
      // A clicked push is read: its payload carries `data.notificationId` (the server's Angular-format payload).
      // The navigation itself is `SparkNotificationClicks`' (pwa), which does not know about notifications.
      subscription.add(this.swPush.notificationClicks.subscribe(({ notification }) => {
        const id = (notification?.data as { notificationId?: unknown } | undefined)?.notificationId;
        if (typeof id === 'string' && id) this.markRead([id]).catch(() => undefined);
      }));
      inject(DestroyRef).onDestroy(() => subscription.unsubscribe());
    }
  }

  /**
   * Marks notifications read: the given ids, or every unread one of the caller when `ids` is omitted.
   * Resolves to how many changed. The live queries then deliver the new state; nothing is set locally.
   */
  async markRead(ids?: readonly string[]): Promise<number> {
    const body = ids ? { ids: [...ids] } : {};
    const response = await firstValueFrom(this.http.post<{ marked: number }>(`${this.baseUrl}/read`, body));
    return response?.marked ?? 0;
  }

  /**
   * Opens a notification: marks it read when it is not, then follows its link, in-app through the router
   * for a URL on this origin, as a page load for another `http(s)` URL. A row without its `Url` (a
   * detail-only attribute in the shipped model) is loaded once for it.
   */
  async open(notification: SparkNotification): Promise<void> {
    const url = notification.url ?? await this.loadUrl(notification.id);
    if (!notification.isRead) {
      try {
        await this.markRead([notification.id]);
      } catch {
        // Following the link matters more than the read flag; the list still shows it unread.
      }
    }
    this.navigate(url);
  }

  /**
   * Turns Web Push on for this device. Call it straight from a click handler: the permission prompt is
   * requested before anything is awaited, because Safari only grants it to a user gesture. Resolves to
   * whether the device is now subscribed; a refusal, a missing key or a subscription the server rejects
   * (400: a push service outside its allow-list) is `false`.
   */
  async enablePush(): Promise<boolean> {
    const permissions = this.permissions;
    const swPush = this.swPush;
    if (!this.pushSupported || !permissions || !swPush) return false;

    const asked = permissions.requestPermission();
    this.busy.set(true);
    try {
      const key = this.loadPublicKey();
      const permission = await asked;
      this.permission.set(permission);
      if (permission !== 'granted') return false;

      const serverPublicKey = await key;
      if (!serverPublicKey) return false;

      const subscription = await swPush.requestSubscription({ serverPublicKey });
      try {
        await firstValueFrom(this.http.post(`${this.baseUrl}/push/subscribe`, subscription.toJSON()));
      } catch {
        // The server refused this device: do not leave a subscription nobody sends to.
        await swPush.unsubscribe().catch(() => undefined);
        this.subscribed.set(false);
        return false;
      }
      this.subscribed.set(true);
      return true;
    } catch {
      return false;
    } finally {
      this.busy.set(false);
    }
  }

  /** Turns Web Push off for this device: the server forgets it, then the browser unsubscribes. */
  async disablePush(): Promise<void> {
    const swPush = this.swPush;
    if (!swPush?.isEnabled) return;
    this.busy.set(true);
    try {
      const current = await firstValueFrom(swPush.subscription);
      if (current) {
        await firstValueFrom(this.http.post(`${this.baseUrl}/push/unsubscribe`, { endpoint: current.endpoint })).catch(() => undefined);
        await swPush.unsubscribe().catch(() => undefined);
      }
      this.subscribed.set(false);
    } finally {
      this.busy.set(false);
    }
  }

  // --- internals ---------------------------------------------------------------------------------

  private follow(request: LiveQueryRequest, target: WritableSignal<QueryResult | null>): Subscription {
    return this.live.watchQuery(request).subscribe(event => {
      target.set(event.type === 'result' ? event.result : null);
      if (target === this.latest && event.type === 'result') this.noticeArrivals(event.result);
    });
  }

  private noticeArrivals(result: QueryResult): void {
    const rows = (result.items ?? []).map(toSparkNotification);
    const first = this.known === null;
    this.known ??= new Set();
    for (const row of rows) {
      if (this.known.has(row.id)) continue;
      this.known.add(row.id);
      if (!first && !row.isRead) this.arrivals.next(serverBellEntry(row));
    }
  }

  // --- the bell ----------------------------------------------------------------------------------

  /** Chooses an entry: a stored one is opened ({@link open}), a session one is marked read. */
  async choose(entry: SparkBellEntry): Promise<void> {
    if (entry.source === 'session') {
      this.session.markRead(entry.id);
      return;
    }
    const notification = this.recent().find(n => n.id === entry.id)
      ?? { id: entry.id, title: entry.title, body: entry.body, url: null, isRead: entry.isRead, createdAt: entry.createdAt };
    await this.open(notification);
  }

  /** Marks everything read: this session's entries at once, the stored ones on the server when any is unread. */
  async markAllRead(): Promise<void> {
    this.session.markAllRead();
    if (this.serverUnreadCount() > 0) await this.markRead();
  }

  /** Removes a session entry; a stored notification is not removed from here. */
  dismiss(entry: SparkBellEntry): void {
    if (entry.source === 'session') this.session.dismiss(entry.id);
  }

  private loadPublicKey(): Promise<string | null> {
    this.publicKey ??= firstValueFrom(this.http.get<{ publicKey: string }>(`${this.baseUrl}/push/key`)).then(
      response => {
        const key = response?.publicKey || null;
        this.configured.set(!!key);
        return key;
      },
      (error: HttpErrorResponse) => {
        this.configured.set(false);
        // A network failure may pass; 404 (not configured) and 401 (signed out) last until the next user.
        if (!(error instanceof HttpErrorResponse) || error.status === 0) this.publicKey = null;
        return null;
      },
    );
    return this.publicKey;
  }

  private async loadUrl(id: string): Promise<string | null> {
    try {
      const po = await this.spark.get(SPARK_NOTIFICATION_TYPE_ID, id);
      const value = po?.attributes?.find(a => a.name === 'Url')?.value;
      return typeof value === 'string' && value ? value : null;
    } catch {
      return null;
    }
  }

  private navigate(url: string | null): void {
    if (!url) return;
    let parsed: URL;
    try {
      parsed = new URL(url, this.document.baseURI);
    } catch {
      return;
    }
    const inApp = sparkInAppUrl(url, this.document, this.location);
    if (inApp && this.router) {
      // A same-origin link the router has no route for is the server's (a PDF download, /bot): load it as a page.
      this.router.navigateByUrl(inApp).catch(() => this.assign(parsed.href));
      return;
    }
    if (parsed.protocol === 'http:' || parsed.protocol === 'https:') this.assign(parsed.href);
  }

  /** A full page load (overridden in specs: jsdom cannot navigate). */
  protected assign(href: string): void {
    this.document.defaultView?.location.assign(href);
  }
}
