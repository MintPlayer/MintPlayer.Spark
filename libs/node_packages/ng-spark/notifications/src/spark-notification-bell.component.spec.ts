import { computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, Subject, merge, map } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { NotificationKind, SparkSessionNotifications } from '@mintplayer/ng-spark/client-operations';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SparkBellEntry, SparkNotification, serverBellEntry, sessionBellEntry } from './spark-notification';
import { SparkNotificationsService, SparkPushState } from './spark-notifications.service';
import { SPARK_POPOUT_DEFAULT_MS, SparkNotificationBellComponent } from './spark-notification-bell.component';
import { SparkPushToggleComponent } from './spark-push-toggle.component';

const notification = (id: string, isRead = false, minutesAgo = 5): SparkNotification => ({
  id, title: `Title ${id}`, body: `Body ${id}`, url: `/runs/${id}`, isRead,
  createdAt: new Date(Date.now() - minutesAgo * 60_000),
});

/** The service's surface, with the real session store so the bell's session behaviour is the shipped one. */
function stubService() {
  const recent = signal<SparkNotification[]>([]);
  const serverUnreadCount = signal(0);
  const serverArrivals = new Subject<SparkBellEntry>();
  const stub = {
    server: true,
    session: null as unknown as SparkSessionNotifications,
    serverUnreadCount,
    // Explicit return types: these read `stub` inside its own initializer (TS7022/TS7024 under the spec type-check).
    unreadCount: computed((): number => serverUnreadCount() + (stub.session?.unreadCount() ?? 0)),
    recent,
    entries: computed((): SparkBellEntry[] => [...recent().map(serverBellEntry), ...(stub.session?.entries() ?? []).map(sessionBellEntry)]
      .sort((a, b) => (b.createdAt?.getTime() ?? 0) - (a.createdAt?.getTime() ?? 0))),
    serverArrivals,
    arrived: null as unknown as Observable<SparkBellEntry>,
    loaded: signal(true),
    pushConfigured: signal<boolean | null>(true),
    pushState: signal<SparkPushState>('off'),
    pushBusy: signal(false),
    pushSupported: true,
    markRead: vi.fn().mockResolvedValue(1),
    open: vi.fn().mockResolvedValue(undefined),
    choose: vi.fn(async (entry: SparkBellEntry): Promise<void> => { if (entry.source === 'session') stub.session.markRead(entry.id); }),
    markAllRead: vi.fn(async (): Promise<void> => { stub.session.markAllRead(); }),
    dismiss: vi.fn((entry: SparkBellEntry): void => { stub.session.dismiss(entry.id); }),
    enablePush: vi.fn().mockResolvedValue(true),
    disablePush: vi.fn().mockResolvedValue(undefined),
  };
  return stub;
}

describe('SparkNotificationBellComponent', () => {
  let service: ReturnType<typeof stubService>;

  function render() {
    TestBed.configureTestingModule({
      imports: [SparkNotificationBellComponent],
      providers: [
        provideRouter([]),
        { provide: SparkNotificationsService, useValue: service },
        { provide: SparkLanguageService, useValue: { t: (_: string, fallback?: string) => fallback, language: signal('en') } },
      ],
    });
    service.session = TestBed.inject(SparkSessionNotifications);
    service.arrived = merge(service.serverArrivals, service.session.arrived.pipe(map(sessionBellEntry)));
    const fixture = TestBed.createComponent(SparkNotificationBellComponent);
    fixture.detectChanges();
    return fixture;
  }

  function openMenu(fixture: ReturnType<typeof render>) {
    (fixture.nativeElement.querySelector('.spark-notification-bell-toggle') as HTMLButtonElement).click();
    fixture.detectChanges();
  }

  const menuRows = () => Array.from(document.querySelectorAll('.spark-notification-menu .spark-notification')) as HTMLElement[];
  const popoutRows = () => Array.from(document.querySelectorAll('.spark-notification-popout .spark-notification')) as HTMLElement[];

  beforeEach(() => {
    service = stubService();
    try { localStorage.removeItem('spark.notifications.shown'); } catch { /* none */ }
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.useRealTimers();
  });

  it('shows the unread count as a badge, live, and none at zero', () => {
    const fixture = render();
    expect(fixture.nativeElement.querySelector('.spark-notification-count')).toBeNull();

    service.serverUnreadCount.set(3);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.spark-notification-count').textContent.trim()).toBe('3');
    expect(fixture.nativeElement.querySelector('.spark-notification-bell-toggle').getAttribute('aria-label')).toBe('Notifications (3)');

    service.serverUnreadCount.set(250);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.spark-notification-count').textContent.trim()).toBe('99+');
  });

  it('counts unread session entries in the badge', () => {
    const fixture = render();
    service.session.add('Saved', NotificationKind.Success);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.spark-notification-count').textContent.trim()).toBe('1');
  });

  it('lists the recent notifications with title, message and relative time', () => {
    service.recent.set([notification('a', false, 5), notification('b', true, 120)]);
    const fixture = render();
    openMenu(fixture);

    const items = menuRows();
    expect(items.map(i => i.getAttribute('data-id'))).toEqual(['a', 'b']);
    expect(items[0].querySelector('.spark-notification-title')!.textContent!.trim()).toBe('Title a');
    expect(items[0].querySelector('.spark-notification-body')!.textContent!.trim()).toBe('Body a');
    expect(items[0].querySelector('.spark-notification-time')!.textContent!.trim()).toBe('5 minutes ago');
    expect(items[1].querySelector('.spark-notification-time')!.textContent!.trim()).toBe('2 hours ago');
    expect(items[0].classList).toContain('spark-notification-unread');
    expect(items[1].classList).not.toContain('spark-notification-unread');
    // A stored notification cannot be closed from the bell.
    expect(items[0].querySelector('.spark-notification-dismiss')).toBeNull();
  });

  it('lists session entries beside the stored ones, newest first, each closable, and reads them on opening', () => {
    service.recent.set([notification('old', false, 60)]);
    const fixture = render();
    service.session.add('Saved', NotificationKind.Success);
    fixture.detectChanges();

    openMenu(fixture);
    const items = menuRows();
    expect(items.map(i => i.getAttribute('data-source'))).toEqual(['session', 'server']);
    expect(items[0].querySelector('.spark-notification-title')!.textContent!.trim()).toBe('Saved');
    expect(service.session.unreadCount()).toBe(0);

    (items[0].querySelector('.spark-notification-dismiss button, .spark-notification-dismiss') as HTMLElement).click();
    fixture.detectChanges();
    expect(service.dismiss).toHaveBeenCalledTimes(1);
    expect(menuRows().map(i => i.getAttribute('data-source'))).toEqual(['server']);
  });

  it('says so when there are none', () => {
    const fixture = render();
    openMenu(fixture);
    expect(document.querySelector('.spark-notification-empty')!.textContent!.trim()).toBe('No notifications');
  });

  it('opens a chosen notification and closes the menu', () => {
    const chosen = notification('a');
    service.recent.set([chosen]);
    const fixture = render();
    openMenu(fixture);

    (menuRows()[0].querySelector('.spark-notification-choose') as HTMLElement).click();
    fixture.detectChanges();

    expect(service.choose).toHaveBeenCalledWith(serverBellEntry(chosen));
    expect(fixture.componentInstance.open()).toBe(false);
  });

  it('marks all as read, offered only while a stored notification is unread', () => {
    const fixture = render();
    openMenu(fixture);
    expect(document.querySelector('.spark-notification-mark-all')).toBeNull();

    service.serverUnreadCount.set(2);
    fixture.detectChanges();
    (document.querySelector('.spark-notification-mark-all') as HTMLElement).click();
    expect(service.markAllRead).toHaveBeenCalledTimes(1);
  });

  it('links to the full list, overridable, and not without the server package', () => {
    let fixture = render();
    openMenu(fixture);
    expect(document.querySelector('.spark-notification-all a')!.getAttribute('href')).toBe('/query/notifications');

    TestBed.resetTestingModule();
    fixture = render();
    fixture.componentRef.setInput('allLink', '/inbox');
    openMenu(fixture);
    expect(document.querySelector('.spark-notification-all a')!.getAttribute('href')).toBe('/inbox');

    TestBed.resetTestingModule();
    service = stubService();
    service.server = false;
    fixture = render();
    openMenu(fixture);
    expect(document.querySelector('.spark-notification-all')).toBeNull();
  });

  it('offers the push opt-in only where this browser can receive push and the server sends it', () => {
    const fixture = render();
    openMenu(fixture);
    expect(document.querySelector('.spark-notification-push .spark-push-enable')).not.toBeNull();

    service.pushState.set('unsupported');
    fixture.detectChanges();
    expect(document.querySelector('.spark-notification-push')).toBeNull();

    service.pushState.set('off');
    service.pushConfigured.set(false);
    fixture.detectChanges();
    expect(document.querySelector('.spark-notification-push')).toBeNull();
  });

  describe('pop-out', () => {
    it('pops an arrival out below the bell for the default time, then hides it; it stays in the bell', () => {
      vi.useFakeTimers();
      const fixture = render();
      service.session.add('Saved', NotificationKind.Success);
      fixture.detectChanges();
      expect(popoutRows().map(r => r.querySelector('.spark-notification-title')!.textContent!.trim())).toEqual(['Saved']);
      expect(document.querySelector('.spark-notification-popout')!.getAttribute('aria-live')).toBe('polite');

      vi.advanceTimersByTime(SPARK_POPOUT_DEFAULT_MS + 1);
      fixture.detectChanges();
      expect(popoutRows()).toHaveLength(0);
      expect(service.session.entries()).toHaveLength(1);
    });

    it('honours a duration, and keeps an error or a duration of 0 until closed', () => {
      vi.useFakeTimers();
      const fixture = render();
      service.session.add('quick', NotificationKind.Info, 1000);
      service.session.add('broken', NotificationKind.Error);
      service.session.add('sticky', NotificationKind.Info, 0);
      fixture.detectChanges();

      vi.advanceTimersByTime(60_000);
      fixture.detectChanges();
      expect(fixture.componentInstance.popped().map(e => e.title)).toEqual(['sticky', 'broken']);

      (popoutRows()[0].querySelector('.spark-notification-dismiss button, .spark-notification-dismiss') as HTMLElement).click();
      fixture.detectChanges();
      expect(fixture.componentInstance.popped().map(e => e.title)).toEqual(['broken']);
      expect(service.session.entries()).toHaveLength(3);
    });

    it('shows at most three, pausing while hovered', () => {
      vi.useFakeTimers();
      const fixture = render();
      for (const m of ['1', '2', '3', '4']) service.session.add(m);
      fixture.detectChanges();
      expect(fixture.componentInstance.popped().map(e => e.title)).toEqual(['4', '3', '2']);

      fixture.componentInstance.pause();
      vi.advanceTimersByTime(SPARK_POPOUT_DEFAULT_MS * 2);
      expect(fixture.componentInstance.popped()).toHaveLength(3);

      fixture.componentInstance.resume();
      vi.advanceTimersByTime(SPARK_POPOUT_DEFAULT_MS + 1);
      expect(fixture.componentInstance.popped()).toHaveLength(0);
    });

    it('pops nothing out while the menu is open, and opening it ends the pop-out', () => {
      const fixture = render();
      service.session.add('first');
      fixture.detectChanges();
      expect(fixture.componentInstance.popped()).toHaveLength(1);

      openMenu(fixture);
      expect(fixture.componentInstance.popped()).toHaveLength(0);
      service.session.add('second');
      fixture.detectChanges();
      expect(fixture.componentInstance.popped()).toHaveLength(0);
    });

    it('pops a stored notification out once per browser', () => {
      const fixture = render();
      const entry = serverBellEntry(notification('n/1'));
      service.serverArrivals.next(entry);
      fixture.componentInstance.unpop(entry);
      service.serverArrivals.next(entry);
      expect(fixture.componentInstance.popped()).toHaveLength(0);
    });

    it('chooses from the pop-out: the entry leaves it and is opened', () => {
      const fixture = render();
      const entry = serverBellEntry(notification('n/2'));
      service.serverArrivals.next(entry);
      fixture.detectChanges();

      (popoutRows()[0].querySelector('.spark-notification-choose') as HTMLElement).click();
      expect(service.choose).toHaveBeenCalledWith(entry);
      expect(fixture.componentInstance.popped()).toHaveLength(0);
    });
  });
});

describe('SparkPushToggleComponent', () => {
  let service: ReturnType<typeof stubService>;

  function render() {
    TestBed.configureTestingModule({
      imports: [SparkPushToggleComponent],
      providers: [
        { provide: SparkNotificationsService, useValue: service },
        { provide: SparkLanguageService, useValue: { t: (key: string) => key, language: signal('en') } },
      ],
    });
    const fixture = TestBed.createComponent(SparkPushToggleComponent);
    fixture.detectChanges();
    return fixture;
  }

  beforeEach(() => { service = stubService(); });
  afterEach(() => TestBed.resetTestingModule());

  it('enables from the click, synchronously', () => {
    const fixture = render();
    (fixture.nativeElement.querySelector('.spark-push-enable') as HTMLButtonElement).click();
    expect(service.enablePush).toHaveBeenCalledTimes(1);
  });

  it('disables when on', () => {
    service.pushState.set('on');
    const fixture = render();
    (fixture.nativeElement.querySelector('.spark-push-disable') as HTMLButtonElement).click();
    expect(service.disablePush).toHaveBeenCalledTimes(1);
  });

  it('explains a blocked or unsupported browser with the translated text', () => {
    service.pushState.set('denied');
    const fixture = render();
    expect(fixture.nativeElement.textContent.trim()).toBe('notifications.spa.pushDenied');

    service.pushState.set('unsupported');
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent.trim()).toBe('notifications.spa.pushUnsupported');
  });

  it('renders nothing while the server does not send Web Push', () => {
    service.pushConfigured.set(false);
    const fixture = render();
    expect(fixture.nativeElement.textContent.trim()).toBe('');
  });
});
