import { describe, expect, it } from 'vitest';
import type { PersistentObject, QueryResultItem } from '@mintplayer/ng-spark/models';
import { valueFor } from '@mintplayer/ng-spark/models';

/**
 * Regression pin for the app's sibling-attribute reads, which moved from a
 * hand-rolled `rowAttr` helper to Spark's `valueFor` in the preview.67 adoption.
 *
 * These cases are the ones `rowAttr` was written to cover — they are kept
 * because `repo-name` (IsPrivate) and `short-sha` (FullName, titleAttribute)
 * fail *silently* when a sibling read comes back empty: the cell still renders,
 * just without its badge, link or tooltip.
 */

function po(attributes: Record<string, unknown>): PersistentObject {
  return {
    attributes: Object.entries(attributes).map(([name, value]) => ({ name, value })),
  } as unknown as PersistentObject;
}

function row(values: Record<string, unknown>): QueryResultItem {
  return {
    id: 'items/1',
    values: Object.entries(values).map(([key, value]) => ({ key, value })),
  } as unknown as QueryResultItem;
}

describe('valueFor', () => {
  // A query grid hands a QueryResultItem — the shape preview.67 introduced, and
  // the one the accounts/repositories/commits grids actually use.
  it('reads a value off a QueryResultItem row', () => {
    expect(valueFor(row({ Sha: 'abc123', Branch: 'master' }), 'Sha')?.value).toBe('abc123');
  });

  it('reads the same value off a PersistentObject row', () => {
    expect(valueFor(po({ Sha: 'abc123', Branch: 'master' }), 'Sha')?.value).toBe('abc123');
  });

  // AsDetail sub-table cells hand the renderer a flat record instead. A renderer
  // that only understood one shape would silently render blank in the other host,
  // which looks like missing data rather than a wiring bug.
  it('reads the same value off a flat record row', () => {
    expect(valueFor({ Sha: 'abc123' }, 'Sha')?.value).toBe('abc123');
  });

  it('returns undefined for a column the row does not carry', () => {
    expect(valueFor(row({ Sha: 'abc123' }), 'Missing')?.value).toBeUndefined();
    expect(valueFor(po({ Sha: 'abc123' }), 'Missing')?.value).toBeUndefined();
    expect(valueFor({ Sha: 'abc123' }, 'Missing')?.value).toBeUndefined();
  });

  it('returns undefined rather than throwing when there is no row', () => {
    expect(valueFor(null, 'Sha')?.value).toBeUndefined();
    expect(valueFor(undefined, 'Sha')?.value).toBeUndefined();
  });

  // A falsy value is a value. Collapsing it to undefined would make "zero lines
  // covered" render as "unknown", and would make a public repo look private.
  it('preserves falsy values', () => {
    expect(valueFor(row({ LinesCovered: 0 }), 'LinesCovered')?.value).toBe(0);
    expect(valueFor(po({ LinesCovered: 0 }), 'LinesCovered')?.value).toBe(0);
    expect(valueFor({ LinesCovered: 0 }, 'LinesCovered')?.value).toBe(0);
    expect(valueFor(row({ IsPrivate: false }), 'IsPrivate')?.value).toBe(false);
  });
});
