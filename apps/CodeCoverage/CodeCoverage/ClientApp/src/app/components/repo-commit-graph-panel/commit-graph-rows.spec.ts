import { describe, expect, it } from 'vitest';
import type { CommitGraphLane, CommitGraphRow } from '../../services/browse.service';
import { branchCoverageSeries, derivePage, firstParentPath, GraphRowView, lanesBelow, pieWedge } from './commit-graph-rows';

/** A wire row as the server's JoinLeft layout emits it; only the topology fields matter here. */
function row(sha: string, parents: string[], lane: number, colour: number, up: number[], down: CommitGraphLane[]): CommitGraphRow {
  return {
    sha, parents, lane, colour, up, down,
    subject: sha, authorName: 'Test Author', date: '2026-01-01T00:00:00Z', refs: [], coverage: null, avatarKey: null,
  };
}

/** `key d colour` per segment, in drawing order: compact enough to read as a golden. */
function golden(views: GraphRowView[]): string[][] {
  return views.map((v) => v.segments.map((s) => `${s.key} ${s.d} ${s.colour}`));
}

describe('commit graph rows', () => {
  it('draws a linear history as one straight lane', () => {
    const views = derivePage([], [
      row('c', ['b'], 0, 0, [], [[0, 0]]),
      row('b', ['a'], 0, 0, [0], [[0, 0]]),
      row('a', [], 0, 0, [0], []),
    ], 0);

    expect(golden(views)).toEqual([
      ['d0 M7 16V32 0'],
      ['u0 M7 0V16 0', 'd0 M7 16V32 0'],
      ['u0 M7 0V16 0'],
    ]);
    expect(views.map((v) => [v.index, v.cx, v.cy, v.width, v.isMerge])).toEqual([
      [0, 7, 16, 1, false], [1, 7, 16, 1, false], [2, 7, 16, 1, false],
    ]);
  });

  // M merges C into B; C's first parent A is already expected by lane 0 to its left, so C bends into it (JoinLeft).
  it('draws a merge: a second lane opens at the merge and joins left at the shared parent', () => {
    const views = derivePage([], [
      row('m', ['b', 'c'], 0, 0, [], [[0, 0], [1, 1]]),
      row('b', ['a'], 0, 0, [0], [[0, 0]]),
      row('c', ['a'], 1, 1, [1], [[0, 0]]),
      row('a', [], 0, 0, [0], []),
    ], 0);

    expect(golden(views)).toEqual([
      ['d0 M7 16V32 0', 'd1 M7 16C7 24 21 24 21 32 1'],
      ['t1 M21 0V32 1', 'u0 M7 0V16 0', 'd0 M7 16V32 0'],
      ['t0 M7 0V32 0', 'u1 M21 0V16 1', 'd0 M21 16C21 24 7 24 7 32 0'],
      ['u0 M7 0V16 0'],
    ]);
    expect(views[0].isMerge).toBe(true);
    expect(views.map((v) => v.width)).toEqual([2, 2, 2, 1]);
  });

  it('draws an octopus merge with one edge per parent', () => {
    const views = derivePage([], [
      row('o', ['p1', 'p2', 'p3'], 0, 0, [], [[0, 0], [1, 1], [2, 2]]),
      row('p1', ['r'], 0, 0, [0], [[0, 0]]),
      row('p2', ['r'], 1, 1, [1], [[0, 0]]),
      row('p3', ['r'], 2, 2, [2], [[0, 0]]),
      row('r', [], 0, 0, [0], []),
    ], 0);

    expect(golden(views)).toEqual([
      ['d0 M7 16V32 0', 'd1 M7 16C7 24 21 24 21 32 1', 'd2 M7 16C7 24 35 24 35 32 2'],
      ['t1 M21 0V32 1', 't2 M35 0V32 2', 'u0 M7 0V16 0', 'd0 M7 16V32 0'],
      ['t0 M7 0V32 0', 't2 M35 0V32 2', 'u1 M21 0V16 1', 'd0 M21 16C21 24 7 24 7 32 0'],
      ['t0 M7 0V32 0', 'u2 M35 0V16 2', 'd0 M35 16C35 24 7 24 7 32 0'],
      ['u0 M7 0V16 0'],
    ]);
    expect(views[0].width).toBe(3);
  });

  // A page fetched on its own (virtual scroll) knows the lanes above it only from its `top` header.
  it('carries pass-throughs across a page boundary from the page header', () => {
    const top: CommitGraphLane[] = [[0, 0], [1, 9]];
    const views = derivePage(top, [
      row('x', ['w'], 0, 0, [0], [[0, 0]]),
      row('y', ['w'], 1, 9, [1], [[0, 0]]),
    ], 200);

    expect(views[0].index).toBe(200);
    expect(golden(views)).toEqual([
      // Colour 9 wraps onto the 8-colour palette.
      ['t1 M21 0V32 1', 'u0 M7 0V16 0', 'd0 M7 16V32 0'],
      ['t0 M7 0V32 0', 'u1 M21 0V16 1', 'd0 M21 16C21 24 7 24 7 32 0'],
    ]);
  });

  it('computes the lanes below a row as (top - up) + down', () => {
    const below = lanesBelow(new Map([[0, 0], [1, 1], [3, 3]]), { up: [1, 3], down: [[1, 1], [2, 4]] });
    expect([...below].sort((a, b) => a[0] - b[0])).toEqual([[0, 0], [1, 1], [2, 4]]);
  });

  describe('first-parent path', () => {
    const views = derivePage([], [
      row('m', ['b', 'c'], 0, 0, [], [[0, 0], [1, 1]]),
      row('b', ['a'], 0, 0, [0], [[0, 0]]),
      row('c', ['a'], 1, 1, [1], [[0, 0]]),
      row('a', [], 0, 0, [0], []),
    ], 0);
    const index = new Map(views.map((v) => [v.row.sha, v.index]));
    const plain = (p: Map<number, Set<string>>) => [...p].map(([i, keys]) => [i, [...keys].sort()]);

    it('follows first parents through the rows in between', () => {
      const path = firstParentPath(0, (i) => views[i], (sha) => index.get(sha));
      expect(plain(path)).toEqual([
        [0, ['d0', 'n']],
        [1, ['d0', 'n', 'u0']],
        [2, ['t0']],
        [3, ['n', 'u0']],
      ]);
    });

    it('stops at a row that is not loaded', () => {
      const path = firstParentPath(0, (i) => (i === 2 ? undefined : views[i]), (sha) => index.get(sha));
      expect(plain(path)).toEqual([[0, ['d0', 'n']], [1, ['n', 'u0']]]);
    });

    it('is empty for a row that is not loaded', () => {
      expect(firstParentPath(9, () => undefined, () => undefined).size).toBe(0);
    });
  });

  describe('branch coverage series', () => {
    const covered = (percent: number | null, completeness: string | null = 'Complete') =>
      ({ linesCovered: 0, linesCoverable: 0, percent, deltaVsParent: null, completeness });
    // m merges c into b; the branch's first-parent chain is m, b, a. c is on the merged-in side.
    const views = derivePage([], [
      { ...row('m', ['b', 'c'], 0, 0, [], [[0, 0], [1, 1]]), coverage: covered(80) },
      { ...row('b', ['a'], 0, 0, [0], [[0, 0]]), coverage: covered(70) },
      { ...row('c', ['a'], 1, 1, [1], [[0, 0]]), coverage: covered(10) },
      { ...row('a', ['z'], 0, 0, [0], [[0, 0]]), coverage: covered(60) },
    ], 0);
    const index = new Map(views.map((v) => [v.row.sha, v.index]));

    it('follows first parents from the tip, oldest first, past the merged-in side', () => {
      expect(branchCoverageSeries('m', (i) => views[i], (sha) => index.get(sha))).toEqual([60, 70, 80]);
    });

    it('skips commits with no complete report and stops at a row that is not loaded', () => {
      const partial = views.map((v) => (v.row.sha === 'b' ? { ...v, row: { ...v.row, coverage: covered(70, 'Partial') } } : v));
      expect(branchCoverageSeries('m', (i) => partial[i], (sha) => index.get(sha))).toEqual([60, 80]);
      expect(branchCoverageSeries('m', (i) => (i === 1 ? undefined : views[i]), (sha) => index.get(sha))).toEqual([80]);
      expect(branchCoverageSeries('unknown', (i) => views[i], (sha) => index.get(sha))).toEqual([]);
    });
  });

  it('draws a coverage wedge clockwise from twelve o\'clock', () => {
    expect(pieWedge(10, 10, 4, 0.25)).toBe('M10 10L10 6A4 4 0 0 1 14 10Z');
    expect(pieWedge(10, 10, 4, 0.75)).toBe('M10 10L10 6A4 4 0 1 1 6 10Z');
  });
});
