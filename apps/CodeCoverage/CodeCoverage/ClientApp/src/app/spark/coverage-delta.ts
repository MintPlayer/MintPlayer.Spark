/**
 * How a Δ cell reads: a signed percentage-point change to one decimal, or a
 * dash when there is no reference commit to compare against. The dash is
 * deliberate — an absent reference used to render as an empty cell, which is
 * indistinguishable from "0.0 hidden by a filter"; and it must never render as
 * 0.0, which claims a comparison that did not happen.
 */
export const NO_DELTA = '—';

export interface DeltaView {
  up: boolean;
  down: boolean;
  text: string;
}

export function formatDelta(raw: unknown): DeltaView {
  const value = typeof raw === 'number' ? raw : raw === null || raw === undefined || raw === '' ? NaN : Number(raw);
  if (Number.isNaN(value)) return { up: false, down: false, text: NO_DELTA };
  return { up: value > 0, down: value < 0, text: `${value > 0 ? '+' : ''}${value.toFixed(1)}` };
}
