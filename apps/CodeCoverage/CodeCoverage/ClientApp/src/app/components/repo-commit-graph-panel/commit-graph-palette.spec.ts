import { describe, expect, it } from 'vitest';
import { contrastRatio, DARK_BACKGROUND, isDarkBackground, LANE_PALETTE, LIGHT_BACKGROUND, relativeLuminance } from './commit-graph-palette';

describe('commit graph palette', () => {
  it('has eight distinct lane colours per theme', () => {
    expect(LANE_PALETTE.length).toBe(8);
    expect(new Set(LANE_PALETTE.map((s) => s.light)).size).toBe(8);
    expect(new Set(LANE_PALETTE.map((s) => s.dark)).size).toBe(8);
  });

  // S9: every lane colour at least 3:1 (WCAG non-text contrast) against its theme's card surface.
  it.each(LANE_PALETTE.map((s, i) => [i, s.light, s.dark]))('lane %i keeps 3:1 in both themes (%s, %s)', (_i, light, dark) => {
    expect(contrastRatio(light as string, LIGHT_BACKGROUND)).toBeGreaterThanOrEqual(3);
    expect(contrastRatio(dark as string, DARK_BACKGROUND)).toBeGreaterThanOrEqual(3);
  });

  it('computes WCAG contrast', () => {
    expect(contrastRatio('#000000', '#ffffff')).toBeCloseTo(21, 5);
    expect(contrastRatio('#ffffff', '#ffffff')).toBeCloseTo(1, 5);
    expect(contrastRatio('rgb(33, 37, 41)', '#212529')).toBeCloseTo(1, 5);
    expect(relativeLuminance('#fff')).toBeCloseTo(1, 5);
    expect(relativeLuminance('not a colour')).toBeNull();
  });

  it('tells the dark surface from the light one', () => {
    expect(isDarkBackground(DARK_BACKGROUND)).toBe(true);
    expect(isDarkBackground('rgb(33, 37, 41)')).toBe(true);
    expect(isDarkBackground(LIGHT_BACKGROUND)).toBe(false);
    expect(isDarkBackground('garbage')).toBe(false);
  });
});
