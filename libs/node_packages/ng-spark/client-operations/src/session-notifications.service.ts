import { Injectable, Signal, computed, isDevMode, signal } from '@angular/core';
import { Observable, Subject } from 'rxjs';
import { NotificationKind } from './operations';

/**
 * One message the server sent this page (a `Notify` client operation, `docs/notification_bell_PRD.md` R9). It lives
 * in memory only: a reload drops it, by definition. The bell lists it beside the server's notifications, and the
 * pop-out shows it as it arrives.
 */
export interface SparkSessionNotification {
    id: string;
    message: string;
    kind: NotificationKind;
    /** How long the pop-out shows it; `0` keeps it until dismissed. `null`: the pop-out's default. */
    durationMs: number | null;
    createdAt: Date;
    isRead: boolean;
}

/** The most entries kept; the oldest go first. */
const MAX_ENTRIES = 50;

/**
 * The session's own notifications: what `Notify` operations brought to this page (#710, one UI system). The built-in
 * `notify` handler of `withClientOperations()` adds here; `<spark-notification-bell>` (`@mintplayer/ng-spark/notifications`)
 * lists them and pops them out. There is no second UI: the toast container is gone.
 */
@Injectable({ providedIn: 'root' })
export class SparkSessionNotifications {
    private readonly _entries = signal<readonly SparkSessionNotification[]>([]);
    private readonly arrivals = new Subject<SparkSessionNotification>();
    private hosts = 0;
    private warned = false;

    /** Newest first. */
    readonly entries: Signal<readonly SparkSessionNotification[]> = this._entries.asReadonly();
    readonly unreadCount: Signal<number> = computed(() => this._entries().filter(e => !e.isRead).length);
    /** Each entry as it is added: the pop-out's feed. */
    readonly arrived: Observable<SparkSessionNotification> = this.arrivals.asObservable();

    add(message: string, kind: NotificationKind = NotificationKind.Info, durationMs?: number | null): SparkSessionNotification {
        const entry: SparkSessionNotification = {
            id: newId(),
            message,
            kind,
            durationMs: durationMs ?? null,
            createdAt: new Date(),
            isRead: false,
        };
        this._entries.update(entries => [entry, ...entries].slice(0, MAX_ENTRIES));
        if (this.hosts === 0 && !this.warned && isDevMode()) {
            this.warned = true;
            console.warn('[spark] A Notify message arrived but no <spark-notification-bell> is rendered, so nobody sees it. '
                + 'Put the bell in the app\'s header (docs/guide-notifications.md).');
        }
        this.arrivals.next(entry);
        return entry;
    }

    markAllRead(): void {
        if (this._entries().some(e => !e.isRead))
            this._entries.update(entries => entries.map(e => e.isRead ? e : { ...e, isRead: true }));
    }

    markRead(id: string): void {
        this._entries.update(entries => entries.map(e => e.id === id && !e.isRead ? { ...e, isRead: true } : e));
    }

    dismiss(id: string): void {
        this._entries.update(entries => entries.filter(e => e.id !== id));
    }

    clear(): void {
        this._entries.set([]);
    }

    /**
     * Called by a component that shows the entries (the bell), for as long as it is rendered: without one, a
     * `Notify` in dev mode logs a warning once. Returns the release.
     */
    attachHost(): () => void {
        this.hosts++;
        let released = false;
        return () => {
            if (!released) {
                released = true;
                this.hosts--;
            }
        };
    }
}

function newId(): string {
    return typeof crypto !== 'undefined' && crypto.randomUUID ? crypto.randomUUID() : `${Date.now()}-${Math.random()}`;
}
