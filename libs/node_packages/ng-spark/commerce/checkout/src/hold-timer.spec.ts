import { describe, expect, it } from 'vitest';
import { crossedMark, formatClock, HOLD_MORE_TIME_FROM, holdTimerState, secondsUntil } from './hold-timer';

// GA10 (WCAG 2.2.1 "Extend"): the hold timer's rules.

const t0 = Date.parse('2026-10-20T12:00:00Z');
const at = (secondsFromT0: number) => new Date(t0 + secondsFromT0 * 1000).toISOString();

describe('hold timer', () => {
  it('counts whole seconds down to the deadline and never below zero', () => {
    expect(secondsUntil(at(90), t0)).toBe(90);
    expect(secondsUntil(at(90), t0 + 500)).toBe(89);
    expect(secondsUntil(at(-5), t0)).toBe(0);
    expect(secondsUntil(null, t0)).toBe(0);
    expect(secondsUntil('not a date', t0)).toBe(0);
  });

  it('formats m:ss and h:mm:ss', () => {
    expect(formatClock(0)).toBe('0:00');
    expect(formatClock(65)).toBe('1:05');
    expect(formatClock(3725)).toBe('1:02:05');
  });

  it('offers "More time" only inside the window, while a step is left, and keeps at least 20 s for it', () => {
    expect(HOLD_MORE_TIME_FROM).toBeGreaterThanOrEqual(20);
    // 10 minutes left: too early.
    expect(holdTimerState(at(600), at(900), t0).canExtend).toBe(false);
    // 4 minutes left with a step left: offered.
    expect(holdTimerState(at(240), at(540), t0).canExtend).toBe(true);
    // At the cap (the server reports no step, or the same deadline): never offered.
    expect(holdTimerState(at(240), null, t0).canExtend).toBe(false);
    expect(holdTimerState(at(240), at(240), t0).canExtend).toBe(false);
    // Lapsed: not offered, and reported as lapsed.
    const lapsed = holdTimerState(at(-1), at(300), t0);
    expect(lapsed.lapsed).toBe(true);
    expect(lapsed.canExtend).toBe(false);
  });

  it('is not lapsed without a deadline', () => {
    expect(holdTimerState(null, null, t0).lapsed).toBe(false);
  });

  it('announces 5 min, 1 min and 20 s on the way down only', () => {
    expect(crossedMark(null, 300)).toBeNull();
    expect(crossedMark(301, 300)).toBe(300);
    expect(crossedMark(300, 299)).toBeNull();
    expect(crossedMark(61, 60)).toBe(60);
    expect(crossedMark(21, 20)).toBe(20);
    expect(crossedMark(19, 18)).toBeNull();
  });

  it('never announces after "More time" (the seconds go up) or once lapsed', () => {
    expect(crossedMark(20, 320)).toBeNull();
    expect(crossedMark(5, 0)).toBeNull();
  });

  it('announces the smallest mark when a throttled tab skips several', () => {
    expect(crossedMark(320, 50)).toBe(60);
    expect(crossedMark(320, 10)).toBe(20);
  });
});
