import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { SparkNotificationService } from './notification.service';
import { NotificationKind } from './operations';

describe('SparkNotificationService', () => {
  let service: SparkNotificationService;

  beforeEach(() => {
    vi.useFakeTimers();
    TestBed.resetTestingModule();
    service = TestBed.inject(SparkNotificationService);
  });

  afterEach(() => vi.useRealTimers());

  it('adds a toast with Info kind and the 4 s default duration', () => {
    service.show('Saved');

    expect(service.toasts()).toEqual([
      { id: expect.any(String), message: 'Saved', kind: NotificationKind.Info, durationMs: 4000 },
    ]);
  });

  it('auto-dismisses after the duration, and not before', () => {
    service.show('Saved', NotificationKind.Success, 1000);

    vi.advanceTimersByTime(999);
    expect(service.toasts()).toHaveLength(1);
    vi.advanceTimersByTime(1);
    expect(service.toasts()).toEqual([]);
  });

  it('keeps a toast with duration 0 until it is dismissed by hand', () => {
    service.show('Sticky', NotificationKind.Error, 0);

    vi.advanceTimersByTime(60_000);
    expect(service.toasts()).toHaveLength(1);

    service.dismiss(service.toasts()[0].id);
    expect(service.toasts()).toEqual([]);
  });

  it('dismisses only the toast it names, keeping order of the rest', () => {
    service.show('a', NotificationKind.Info, 0);
    service.show('b', NotificationKind.Warning, 0);
    service.show('c', NotificationKind.Info, 0);
    const [a, b, c] = service.toasts();

    expect(new Set([a.id, b.id, c.id]).size).toBe(3);
    service.dismiss(b.id);
    expect(service.toasts().map(t => t.message)).toEqual(['a', 'c']);
  });

  it('an auto-dismiss for an already-dismissed toast removes nothing else', () => {
    service.show('first', NotificationKind.Info, 100);
    service.dismiss(service.toasts()[0].id);
    service.show('second', NotificationKind.Info, 0);

    vi.advanceTimersByTime(100);
    expect(service.toasts().map(t => t.message)).toEqual(['second']);
  });

  it('clear() removes every toast', () => {
    service.show('a', NotificationKind.Info, 0);
    service.show('b', NotificationKind.Info, 0);

    service.clear();
    expect(service.toasts()).toEqual([]);
  });

  it('falls back to a generated id when crypto.randomUUID is unavailable', () => {
    vi.stubGlobal('crypto', {});
    try {
      service.show('a', NotificationKind.Info, 0);
      service.show('b', NotificationKind.Info, 0);
    } finally {
      vi.unstubAllGlobals();
    }

    const [a, b] = service.toasts();
    expect(a.id).toMatch(/^\d+-0\.\d+/);
    expect(a.id).not.toBe(b.id);
  });
});
