import { describe, expect, it } from 'vitest';
import type { PersistentObject } from '@mintplayer/ng-spark/models';
import { toCoverageSummary } from './coverage-summary';

/** The nested shape Spark delivers for an AsDetail attribute (Spark#241). */
function po(attributes: Record<string, unknown>): PersistentObject {
  return {
    attributes: Object.entries(attributes).map(([name, value]) => ({ name, value })),
  } as unknown as PersistentObject;
}

describe('toCoverageSummary', () => {
  it('reads the nested PersistentObject shape', () => {
    const summary = toCoverageSummary(
      po({
        LinesCovered: 75,
        LinesCoverable: 100,
        BranchesCovered: 8,
        BranchesTotal: 20,
        FilesCount: 5,
      }),
    );

    expect(summary).toEqual({
      linesCovered: 75,
      linesCoverable: 100,
      branchesCovered: 8,
      branchesTotal: 20,
      filesCount: 5,
    });
  });

  // Spark sends attribute values as strings; a summary that arrived as text would
  // otherwise concatenate rather than add wherever these numbers are used.
  it('coerces string attribute values to numbers', () => {
    const summary = toCoverageSummary(
      po({ LinesCovered: '75', LinesCoverable: '100', FilesCount: '5' }),
    );

    expect(summary?.linesCovered).toBe(75);
    expect(summary?.linesCoverable).toBe(100);
  });

  it('defaults absent branch attributes to zero rather than NaN', () => {
    const summary = toCoverageSummary(po({ LinesCovered: 1, LinesCoverable: 2, FilesCount: 1 }));

    expect(summary?.branchesCovered).toBe(0);
    expect(summary?.branchesTotal).toBe(0);
  });

  it('reads the flat camelCase shape the /api endpoints deliver', () => {
    const dict = {
      linesCovered: 10,
      linesCoverable: 20,
      branchesCovered: 1,
      branchesTotal: 2,
      filesCount: 3,
    };

    expect(toCoverageSummary(dict)).toBe(dict);
  });

  // An empty AsDetail arrives as a PersistentObject full of zeroes rather than as
  // null. Returning a zeroed summary would render as 0% — indistinguishable from a
  // commit that genuinely covers nothing.
  it('treats a wholly empty summary as no data', () => {
    expect(toCoverageSummary(po({ LinesCoverable: 0, FilesCount: 0 }))).toBeNull();
  });

  // Files with no coverable lines are still data: the commit was measured.
  it('keeps a summary that has files but no coverable lines', () => {
    const summary = toCoverageSummary(po({ LinesCoverable: 0, FilesCount: 3 }));

    expect(summary?.filesCount).toBe(3);
  });

  it('returns null for absent values', () => {
    expect(toCoverageSummary(null)).toBeNull();
    expect(toCoverageSummary(undefined)).toBeNull();
  });

  it('returns null for a flat object that is not a summary', () => {
    expect(toCoverageSummary({ somethingElse: 1 })).toBeNull();
  });
});
