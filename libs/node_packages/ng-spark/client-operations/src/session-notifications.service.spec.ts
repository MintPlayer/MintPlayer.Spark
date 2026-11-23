import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { SparkSessionNotifications } from './session-notifications.service';
import { NotificationKind } from './operations';

describe('SparkSessionNotifications', () => {
  let service: SparkSessionNotifications;

  beforeEach(() => {
    TestBed.resetTestingModule();
    service = TestBed.inject(SparkSessionNotifications);
  });

  afterEach(() => vi.restoreAllMocks());

  it('adds an unread Info entry, newest first, with no duration of its own', () => {
    vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    service.add('first');
    service.add('second', NotificationKind.Error, 0);

    expect(service.entries().map(e => [e.message, e.kind, e.durationMs, e.isRead])).toEqual([
      ['second', NotificationKind.Error, 0, false],
      ['first', NotificationKind.Info, null, false],
    ]);
    expect(service.unreadCount()).toBe(2);
  });

  it('keeps entries until dismissed: a timer belongs to the pop-out, not the list', () => {
    vi.useFakeTimers();
    try {
      const release = service.attachHost();
      service.add('Saved', NotificationKind.Success, 1000);
      vi.advanceTimersByTime(60_000);
      expect(service.entries()).toHaveLength(1);
      release();
    } finally {
      vi.useRealTimers();
    }
  });

  it('marks read one or all, and dismisses only the entry it names', () => {
    const release = service.attachHost();
    const a = service.add('a');
    const b = service.add('b');
    service.add('c');

    service.markRead(a.id);
    expect(service.unreadCount()).toBe(2);
    service.dismiss(b.id);
    expect(service.entries().map(e => e.message)).toEqual(['c', 'a']);
    service.markAllRead();
    expect(service.unreadCount()).toBe(0);
    service.clear();
    expect(service.entries()).toEqual([]);
    release();
  });

  it('emits each entry as it arrives', () => {
    const release = service.attachHost();
    const seen: string[] = [];
    const subscription = service.arrived.subscribe(e => seen.push(e.message));

    service.add('one');
    service.add('two');

    expect(seen).toEqual(['one', 'two']);
    subscription.unsubscribe();
    release();
  });

  it('keeps at most 50 entries, dropping the oldest', () => {
    const release = service.attachHost();
    for (let i = 0; i < 55; i++) service.add(`m${i}`);

    expect(service.entries()).toHaveLength(50);
    expect(service.entries()[0].message).toBe('m54');
    expect(service.entries()[49].message).toBe('m5');
    release();
  });

  it('warns once in dev mode when no bell is rendered, and not while one is', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const release = service.attachHost();
    service.add('seen');
    expect(warn).not.toHaveBeenCalled();
    release();
    release(); // a second release is a no-op

    service.add('unseen');
    service.add('unseen again');
    expect(warn).toHaveBeenCalledTimes(1);
    expect(String(warn.mock.calls[0][0])).toContain('<spark-notification-bell>');
  });

  it('falls back to a generated id when crypto.randomUUID is unavailable', () => {
    const release = service.attachHost();
    vi.stubGlobal('crypto', {});
    try {
      service.add('a');
      service.add('b');
    } finally {
      vi.unstubAllGlobals();
    }

    const [b, a] = service.entries();
    expect(a.id).toMatch(/^\d+-0\.\d+/);
    expect(a.id).not.toBe(b.id);
    release();
  });
});
