import { describe, expect, it } from 'vitest';

import { AS_DETAIL_BREADCRUMBS_KEY, nestedPoToDisplayRow } from './as-detail-conversions';
import { isPersistentObject, isQueryRow, valueFor } from './query-result';
import type { PersistentObject } from './persistent-object';
import type { QueryResultItem } from './query-result';

/**
 * `valueFor` reads a value out of a row **whatever shape the row is in**.
 *
 * There are three, and they are genuinely different objects rather than variations of one: a query
 * grid hands a `QueryResultItem`, an AsDetail sub-table hands the flat record `nestedPoToDisplayRow`
 * builds, and a detail or form host hands the `PersistentObject` itself. A renderer reused across a
 * grid and an AsDetail table sees two of them.
 *
 * Before this, the helper was typed for the first shape only — so the case that most needed it, a
 * shared renderer, could not use it and every app wrote the branch itself. These tests exist so the
 * branch stays inside the framework.
 */

const queryRow: QueryResultItem = {
  id: 'repos/1',
  breadcrumb: 'spark',
  values: [
    { key: 'Name', value: 'spark' },
    { key: 'IsPrivate', value: true },
    { key: 'Owner', value: 'people/7', objectId: 'people/7', breadcrumb: 'Ada Lovelace' },
  ],
};

const persistentObject = {
  id: 'repos/1',
  name: 'spark',
  objectTypeId: 't/1',
  attributes: [
    { id: 'a1', name: 'Name', value: 'spark', dataType: 'string', isRequired: false, isVisible: true, isReadOnly: false, order: 1 },
    { id: 'a2', name: 'IsPrivate', value: true, dataType: 'boolean', isRequired: false, isVisible: true, isReadOnly: false, order: 2 },
    {
      id: 'a3', name: 'Owner', value: 'people/7', dataType: 'Reference', breadcrumb: 'Ada Lovelace',
      isRequired: false, isVisible: true, isReadOnly: false, order: 3,
    },
  ],
} as PersistentObject;

describe('valueFor', () => {
  describe('a query row', () => {
    it('reads a value by column name', () => {
      expect(valueFor(queryRow, 'IsPrivate')?.value).toBe(true);
    });

    it('returns the cell, so a reference keeps its objectId', () => {
      const cell = valueFor(queryRow, 'Owner');
      expect(cell?.value).toBe('people/7');
      expect(cell?.objectId).toBe('people/7');
      expect(cell?.breadcrumb).toBe('Ada Lovelace');
    });

    it('returns undefined for a column the row does not carry', () => {
      // The common cause: the attribute is `showedOn: PersistentObject`, so it is not on the query
      // surface at all. Distinguishing "absent" from "null" is the point of returning undefined.
      expect(valueFor(queryRow, 'Nope')).toBeUndefined();
    });
  });

  describe('a persistent object', () => {
    it('reads a value by attribute name', () => {
      expect(valueFor(persistentObject, 'IsPrivate')?.value).toBe(true);
    });

    it('derives objectId for a single reference, whose value IS the target id', () => {
      // A PO attribute has no objectId field — the value is the id. Deriving it here is what lets
      // one renderer read a reference the same way on a detail page and in a grid.
      const cell = valueFor(persistentObject, 'Owner');
      expect(cell?.objectId).toBe('people/7');
      expect(cell?.breadcrumb).toBe('Ada Lovelace');
    });

    it('does not invent an objectId for a non-reference', () => {
      expect(valueFor(persistentObject, 'Name')?.objectId).toBeUndefined();
    });
  });

  describe('an AsDetail row', () => {
    const row = nestedPoToDisplayRow(persistentObject);

    it('reads a value by column name', () => {
      expect(valueFor(row, 'Name')?.value).toBe('spark');
    });

    it('carries the reference breadcrumb from the side channel', () => {
      const cell = valueFor(row, 'Owner');
      expect(cell?.breadcrumb).toBe('Ada Lovelace');
      expect(cell?.objectId).toBe('people/7');
    });

    it('returns undefined for an absent key rather than a cell with undefined value', () => {
      expect(valueFor(row, 'Nope')).toBeUndefined();
    });

    it('does not mistake the reserved breadcrumb key for a column', () => {
      expect(valueFor(row, AS_DETAIL_BREADCRUMBS_KEY)?.value).toBeDefined();
    });
  });

  describe('shape detection', () => {
    it('tells the three apart', () => {
      expect(isQueryRow(queryRow)).toBe(true);
      expect(isQueryRow(persistentObject)).toBe(false);
      expect(isPersistentObject(persistentObject)).toBe(true);
      expect(isPersistentObject(queryRow)).toBe(false);
    });

    /**
     * ⚠️ The reason detection looks at the ELEMENTS and not just `Array.isArray`. A flat AsDetail
     * record may legitimately have a column named `values` holding an array — an AsDetail array
     * column called exactly that — and reading it as a query row would silently return the wrong
     * thing rather than fail.
     */
    it('does not mistake a flat row with a column named "values" for a query row', () => {
      const awkward = { values: [{ Something: 1 }], Name: 'flat' };

      expect(isQueryRow(awkward)).toBe(false);
      expect(valueFor(awkward, 'Name')?.value).toBe('flat');
    });

    it('does not mistake a flat row with a column named "attributes" for a persistent object', () => {
      const awkward = { attributes: [{ Something: 1 }], Name: 'flat' };

      expect(isPersistentObject(awkward)).toBe(false);
      expect(valueFor(awkward, 'Name')?.value).toBe('flat');
    });
  });

  it('is null-safe', () => {
    expect(valueFor(null, 'Name')).toBeUndefined();
    expect(valueFor(undefined, 'Name')).toBeUndefined();
  });
});
