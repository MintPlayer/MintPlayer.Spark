import { describe, expect, it } from 'vitest';
import { NO_DELTA, formatDelta } from './coverage-delta';

describe('formatDelta', () => {
  it('renders a dash, not zero, when there is no reference', () => {
    expect(formatDelta(null)).toEqual({ up: false, down: false, text: NO_DELTA });
    expect(formatDelta(undefined).text).toBe(NO_DELTA);
    expect(formatDelta('').text).toBe(NO_DELTA);
  });

  it('signs and rounds a change to one decimal', () => {
    expect(formatDelta(1.26)).toEqual({ up: true, down: false, text: '+1.3' });
    expect(formatDelta(-0.34)).toEqual({ up: false, down: true, text: '-0.3' });
  });

  it('treats an exact zero as a real, neutral comparison', () => {
    expect(formatDelta(0)).toEqual({ up: false, down: false, text: '0.0' });
  });

  it('accepts numeric strings from the grid', () => {
    expect(formatDelta('2.5').text).toBe('+2.5');
  });
});
