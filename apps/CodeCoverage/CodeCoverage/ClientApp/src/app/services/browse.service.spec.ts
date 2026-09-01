import { describe, expect, it } from 'vitest';
import { coveragePercent } from './browse.service';

describe('coveragePercent', () => {
  it('reports covered lines as a percentage of coverable lines', () => {
    expect(coveragePercent({ linesCovered: 75, linesCoverable: 100 } as never)).toBe(75);
    expect(coveragePercent({ linesCovered: 1, linesCoverable: 3 } as never)).toBeCloseTo(33.333, 3);
  });

  // The upload contract is explicit that 0/0 is no data, not full coverage. This
  // is the front-end half of that rule: a repository with nothing measured must
  // render as "—", never as a green 100% bar.
  it('returns null when nothing is coverable', () => {
    expect(coveragePercent({ linesCovered: 0, linesCoverable: 0 } as never)).toBeNull();
  });

  it('returns null when there is no summary at all', () => {
    expect(coveragePercent(null)).toBeNull();
    expect(coveragePercent(undefined)).toBeNull();
  });

  it('reports zero for a measured file that covers nothing', () => {
    expect(coveragePercent({ linesCovered: 0, linesCoverable: 40 } as never)).toBe(0);
  });
});
