/**
 * The hold timer's rules (GA10, PRD §4.14; WCAG 2.2 SC 2.2.1 "Timing Adjustable", the "Extend" option), kept free of
 * Angular so they are unit-tested on their own.
 *
 * - The deadline is always shown as text ("Reserved until 14:32"), not only as a running clock.
 * - The countdown is announced through a **polite** live region at {@link HOLD_ANNOUNCE_AT} only (5 min, 1 min, 20 s),
 *   never every second.
 * - "More time" is offered from {@link HOLD_MORE_TIME_FROM} seconds before the deadline, so the buyer always has well over
 *   the 20 s SC 2.2.1 asks for, and as long as the server says a step is left (`extendableUntil`, capped at `Hold:Max`).
 *
 * ⚠️ The SC 2.2.1 reading (20 s, "simple action", the real-time/essential exception beyond `Hold:Max`) follows the
 * normative SC text; W3C's *Understanding SC 2.2.1* was **not** re-read for this implementation (D37: unconfirmed).
 */

/** The seconds-left marks the live region announces (descending). */
export const HOLD_ANNOUNCE_AT: readonly number[] = [300, 60, 20];

/** From how many seconds before the deadline "More time" is shown. Must stay ≥ 20 (SC 2.2.1). */
export const HOLD_MORE_TIME_FROM = 300;

/** The timer's state at one instant. */
export interface HoldTimerState {
  /** Whole seconds left; 0 once the hold lapsed. */
  secondsLeft: number;
  /** `m:ss` (or `h:mm:ss` beyond an hour) of {@link secondsLeft}. */
  clock: string;
  /** The hold lapsed. */
  lapsed: boolean;
  /** "More time" is offered now. */
  canExtend: boolean;
}

/** Whole seconds from `nowMs` to the ISO `deadline`, never negative; 0 for a missing or unreadable deadline. */
export function secondsUntil(deadline: string | null | undefined, nowMs: number): number {
  if (!deadline) return 0;
  const at = Date.parse(deadline);
  if (Number.isNaN(at)) return 0;
  return Math.max(0, Math.floor((at - nowMs) / 1000));
}

/** `m:ss`, or `h:mm:ss` from an hour on. */
export function formatClock(seconds: number): string {
  const s = Math.max(0, Math.floor(seconds));
  const hours = Math.floor(s / 3600);
  const minutes = Math.floor((s % 3600) / 60);
  const rest = String(s % 60).padStart(2, '0');
  return hours > 0 ? `${hours}:${String(minutes).padStart(2, '0')}:${rest}` : `${minutes}:${rest}`;
}

/** The state for `deadline` at `nowMs`; `extendableUntil` is the server's next deadline (null: no step left). */
export function holdTimerState(deadline: string | null | undefined, extendableUntil: string | null | undefined, nowMs: number): HoldTimerState {
  const secondsLeft = secondsUntil(deadline, nowMs);
  const lapsed = !!deadline && secondsLeft === 0;
  const extends_ = !!extendableUntil && !!deadline && Date.parse(extendableUntil) > Date.parse(deadline);
  return {
    secondsLeft,
    clock: formatClock(secondsLeft),
    lapsed,
    canExtend: !lapsed && extends_ && secondsLeft <= HOLD_MORE_TIME_FROM,
  };
}

/**
 * The mark crossed between two ticks (`previous` → `current` seconds left), or null. Only a downward crossing counts,
 * so an extension (the seconds jump up) never announces, and a page opened at 45 s left announces nothing until 20 s.
 * When a throttled background tab skips several marks at once, the smallest one is announced (the true situation).
 */
export function crossedMark(previous: number | null, current: number): number | null {
  if (previous === null || current >= previous || current <= 0) return null;
  let crossed: number | null = null;
  for (const mark of HOLD_ANNOUNCE_AT) {
    if (previous > mark && current <= mark) crossed = mark;
  }
  return crossed;
}
