import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SparkNotification } from './spark-notification';
import { SparkNotificationsService, SparkPushState } from './spark-notifications.service';
import { SparkNotificationBellComponent } from './spark-notification-bell.component';
import { SparkPushToggleComponent } from './spark-push-toggle.component';

const notification = (id: string, isRead = false, minutesAgo = 5): SparkNotification => ({
  id, title: `Title ${id}`, body: `Body ${id}`, url: `/runs/${id}`, isRead,
  createdAt: new Date(Date.now() - minutesAgo * 60_000),
});

function stubService() {
  return {
    unreadCount: signal(0),
    recent: signal<SparkNotification[]>([]),
    loaded: signal(true),
    pushConfigured: signal<boolean | null>(true),
    pushState: signal<SparkPushState>('off'),
    pushBusy: signal(false),
    pushSupported: true,
    markRead: vi.fn().mockResolvedValue(1),
    open: vi.fn().mockResolvedValue(undefined),
    enablePush: vi.fn().mockResolvedValue(true),
    disablePush: vi.fn().mockResolvedValue(undefined),
  };
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
    const fixture = TestBed.createComponent(SparkNotificationBellComponent);
    fixture.detectChanges();
    return fixture;
  }

  function openMenu(fixture: ReturnType<typeof render>) {
    (fixture.nativeElement.querySelector('.spark-notification-bell-toggle') as HTMLButtonElement).click();
    fixture.detectChanges();
  }

  beforeEach(() => { service = stubService(); });
  afterEach(() => TestBed.resetTestingModule());

  it('shows the unread count as a badge, live, and none at zero', () => {
    const fixture = render();
    expect(fixture.nativeElement.querySelector('.spark-notification-count')).toBeNull();

    service.unreadCount.set(3);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.spark-notification-count').textContent.trim()).toBe('3');
    expect(fixture.nativeElement.querySelector('.spark-notification-bell-toggle').getAttribute('aria-label')).toBe('Notifications (3)');

    service.unreadCount.set(250);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.spark-notification-count').textContent.trim()).toBe('99+');
  });

  it('lists the recent notifications with title, message and relative time', () => {
    service.recent.set([notification('a', false, 5), notification('b', true, 120)]);
    const fixture = render();
    openMenu(fixture);

    const items = Array.from(document.querySelectorAll('.spark-notification')) as HTMLElement[];
    expect(items.map(i => i.getAttribute('data-id'))).toEqual(['a', 'b']);
    expect(items[0].querySelector('.spark-notification-title')!.textContent!.trim()).toBe('Title a');
    expect(items[0].querySelector('.spark-notification-body')!.textContent!.trim()).toBe('Body a');
    expect(items[0].querySelector('.spark-notification-time')!.textContent!.trim()).toBe('5 minutes ago');
    expect(items[1].querySelector('.spark-notification-time')!.textContent!.trim()).toBe('2 hours ago');
    expect(items[0].classList).toContain('spark-notification-unread');
    expect(items[1].classList).not.toContain('spark-notification-unread');
  });

  it('says so when there are none', () => {
    const fixture = render();
    openMenu(fixture);
    expect(document.querySelector('.spark-notification-empty')!.textContent!.trim()).toBe('No notifications');
  });

  it('opens a chosen notification (mark read + navigate) and closes the menu', () => {
    const chosen = notification('a');
    service.recent.set([chosen]);
    const fixture = render();
    openMenu(fixture);

    (document.querySelector('.spark-notification') as HTMLElement).click();
    fixture.detectChanges();

    expect(service.open).toHaveBeenCalledWith(chosen);
    expect(fixture.componentInstance.open()).toBe(false);
  });

  it('marks all as read, offered only while something is unread', () => {
    const fixture = render();
    openMenu(fixture);
    expect(document.querySelector('.spark-notification-mark-all')).toBeNull();

    service.unreadCount.set(2);
    fixture.detectChanges();
    (document.querySelector('.spark-notification-mark-all') as HTMLElement).click();
    expect(service.markRead).toHaveBeenCalledWith();
  });

  it('links to the full list, overridable', () => {
    let fixture = render();
    openMenu(fixture);
    expect(document.querySelector('.spark-notification-all a')!.getAttribute('href')).toBe('/query/notifications');

    TestBed.resetTestingModule();
    fixture = render();
    fixture.componentRef.setInput('allLink', '/inbox');
    openMenu(fixture);
    expect(document.querySelector('.spark-notification-all a')!.getAttribute('href')).toBe('/inbox');
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
