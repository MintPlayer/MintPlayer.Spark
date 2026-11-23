import { InjectionToken, inject } from '@angular/core';
import { sparkFeature, SparkFeature, SparkFeatureOrder } from '@mintplayer/ng-spark';
import { DOCUMENT } from '@angular/common';
import { QueryResultItem, valueFor } from '@mintplayer/ng-spark/models';
import { NotificationKind, SparkSessionNotification } from '@mintplayer/ng-spark/client-operations';

/** The `Notification` entity type of `MintPlayer.Spark.Notifications` (`App_Data/Model/Notification.json`). */
export const SPARK_NOTIFICATION_TYPE_ID = '411e3be6-5da0-5d84-bc21-001888fafc6d';
/** `MyNotifications` (alias `notifications`): the caller's notifications, newest first. Live. */
export const SPARK_MY_NOTIFICATIONS_QUERY_ID = '7123c201-9aed-50f9-9d9a-ede2491af7f9';
/** `UnreadNotifications` (alias `unread-notifications`): the caller's unread ones. Live. */
export const SPARK_UNREAD_NOTIFICATIONS_QUERY_ID = 'a205d3e5-ff6e-5260-ac9e-e5c6b16644c9';

/** One notification as the bell lists it: a row of `MyNotifications`. */
export interface SparkNotification {
  id: string;
  title: string;
  body: string | null;
  /**
   * The link, when the row carries it. `Url` is a detail-only attribute in the shipped model, so a row
   * usually does not, and {@link SparkNotificationsService.open} loads it from the object instead.
   */
  url: string | null;
  isRead: boolean;
  createdAt: Date | null;
}

/**
 * One line of the bell, whichever channel it came from (#710: one UI system): a stored notification (`server`,
 * a row of `MyNotifications`) or a `notify` client operation of this session (`session`, kept in memory only).
 */
export interface SparkBellEntry {
  /** Unique across both sources: `server:<id>` or `session:<id>`. */
  key: string;
  source: 'server' | 'session';
  id: string;
  title: string;
  body: string | null;
  /** The client operation's kind; `null` for a stored notification, which has none. */
  kind: NotificationKind | null;
  isRead: boolean;
  createdAt: Date | null;
  /** How long the pop-out shows it: `null` is the default, `0` keeps it until closed. */
  durationMs: number | null;
}

/** A stored notification as a {@link SparkBellEntry}. */
export function serverBellEntry(notification: SparkNotification): SparkBellEntry {
  return {
    key: `server:${notification.id}`, source: 'server', id: notification.id, title: notification.title, body: notification.body,
    kind: null, isRead: notification.isRead, createdAt: notification.createdAt, durationMs: null,
  };
}

/** A session notification as a {@link SparkBellEntry}. */
export function sessionBellEntry(notification: SparkSessionNotification): SparkBellEntry {
  return {
    key: `session:${notification.id}`, source: 'session', id: notification.id, title: notification.message, body: null,
    kind: notification.kind, isRead: notification.isRead, createdAt: notification.createdAt, durationMs: notification.durationMs,
  };
}

/** A query row of `MyNotifications` as a {@link SparkNotification}. */
export function toSparkNotification(item: QueryResultItem): SparkNotification {
  const text = (name: string): string | null => {
    const value = valueFor(item, name)?.value;
    return value == null || value === '' ? null : String(value);
  };
  const created = text('CreatedAtUtc');
  const createdAt = created ? new Date(created) : null;
  return {
    id: item.id,
    title: text('Title') ?? item.breadcrumb ?? '',
    body: text('Body'),
    url: text('Url'),
    isRead: valueFor(item, 'IsRead')?.value === true,
    createdAt: createdAt && !Number.isNaN(createdAt.getTime()) ? createdAt : null,
  };
}

/** Options of the notification bell and service. */
export interface SparkNotificationsOptions {
  /** How many of the latest notifications the bell lists. Default 8. */
  recentCount: number;
  /** Where "All notifications" goes: a router link. Default `['/query', 'notifications']` (the `MyNotifications` alias). */
  listLink: string | readonly unknown[];
  /**
   * Whether the server runs `MintPlayer.Spark.Notifications`, so the bell follows the stored notifications as well
   * as this session's. `false` by default, `true` through {@link withNotifications}: an app that only renders the
   * bell for its client operations (`notify`) needs neither the server package nor this feature (#710 G6).
   */
  server: boolean;
}

export const SPARK_NOTIFICATIONS_OPTIONS = new InjectionToken<SparkNotificationsOptions>('SPARK_NOTIFICATIONS_OPTIONS', {
  providedIn: 'root',
  factory: () => ({ recentCount: 8, listLink: ['/query', 'notifications'], server: false }),
});

/**
 * The server's notifications in the bell, for `provideSpark(...)`; anything left out keeps its default. Leave it out
 * when the server has no `AddNotifications()`: the bell then lists only this session's client operations.
 */
export function withNotifications(options: Partial<SparkNotificationsOptions> = {}): SparkFeature {
  return sparkFeature('notifications', SparkFeatureOrder.modules, [{
    provide: SPARK_NOTIFICATIONS_OPTIONS,
    useValue: { recentCount: 8, listLink: ['/query', 'notifications'], server: true, ...options } satisfies SparkNotificationsOptions,
  }]);
}

/** The browser's Notification API, as far as push needs it. */
export interface SparkNotificationPermissions {
  readonly permission: NotificationPermission;
  requestPermission(): Promise<NotificationPermission>;
}

/**
 * What the browser offers for Web Push: the Notification API, or `null` when there is no Notification,
 * no `PushManager` or no service worker. That is the case in Safari on iPhone and iPad until the app is
 * added to the Home Screen, so `null` reads as "install the app first". Replaceable for tests.
 */
export const SPARK_PUSH_PERMISSIONS = new InjectionToken<SparkNotificationPermissions | null>('SPARK_PUSH_PERMISSIONS', {
  providedIn: 'root',
  factory: () => {
    const window = inject(DOCUMENT).defaultView as (Window & { Notification?: SparkNotificationPermissions; PushManager?: unknown }) | null;
    if (!window || !window.Notification || !window.PushManager || !window.navigator?.serviceWorker) return null;
    return window.Notification;
  },
});
