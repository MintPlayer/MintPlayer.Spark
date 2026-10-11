import { InjectionToken, Provider, inject } from '@angular/core';
import { DOCUMENT } from '@angular/common';
import { QueryResultItem, valueFor } from '@mintplayer/ng-spark/models';

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
}

export const SPARK_NOTIFICATIONS_OPTIONS = new InjectionToken<SparkNotificationsOptions>('SPARK_NOTIFICATIONS_OPTIONS', {
  providedIn: 'root',
  factory: () => ({ recentCount: 8, listLink: ['/query', 'notifications'] }),
});

/** Overrides {@link SparkNotificationsOptions}; anything left out keeps its default. */
export function provideSparkNotifications(options: Partial<SparkNotificationsOptions> = {}): Provider {
  return {
    provide: SPARK_NOTIFICATIONS_OPTIONS,
    useValue: { recentCount: 8, listLink: ['/query', 'notifications'], ...options } satisfies SparkNotificationsOptions,
  };
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
