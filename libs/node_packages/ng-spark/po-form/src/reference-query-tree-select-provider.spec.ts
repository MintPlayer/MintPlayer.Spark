import { describe, expect, it, vi } from 'vitest';
import { QueryResult, QueryResultItem } from '@mintplayer/ng-spark/models';

import {
  ReferenceQueryPageLoader,
  ReferenceQueryTreeSelectProvider,
  referenceTreeNodeMayHaveChildren,
} from './reference-query-tree-select-provider';

const req = () => ({ signal: new AbortController().signal });

function result(items: QueryResultItem[], totalItems = items.length): QueryResult {
  return { columns: [], items, totalItems, skip: 0, take: 50 } as QueryResult;
}

const row = (id: string, childCount?: unknown): QueryResultItem => ({
  id,
  breadcrumb: id.toUpperCase(),
  values: childCount === undefined ? [] : [{ key: 'ChildCount', value: childCount }],
});

describe('ReferenceQueryTreeSelectProvider', () => {
  describe('flat (no referenceTree)', () => {
    it('loads roots without a column filter, and no node is lazy', async () => {
      const load = vi.fn<ReferenceQueryPageLoader>().mockResolvedValue(result([row('a'), row('b')]));
      const provider = new ReferenceQueryTreeSelectProvider(load);

      const page = await provider.loadRoots(req());

      expect(load).toHaveBeenCalledWith({ skip: 0, take: 50, search: undefined });
      expect(page.nodes.map(n => n.id)).toEqual(['a', 'b']);
      expect(page.nodes.every(n => !n.lazy)).toBe(true);
    });

    it('has no children and never queries for them', async () => {
      const load = vi.fn<ReferenceQueryPageLoader>();
      const provider = new ReferenceQueryTreeSelectProvider(load);

      expect(await provider.loadChildren('a', req())).toEqual({ nodes: [] });
      expect(load).not.toHaveBeenCalled();
    });
  });

  describe('hierarchical (referenceTree)', () => {
    const tree = { parentAttribute: 'Parent', childCountAttribute: 'ChildCount' };

    it('loads the roots as the rows with an empty parent', async () => {
      const load = vi.fn<ReferenceQueryPageLoader>().mockResolvedValue(result([row('site', 3)]));
      const provider = new ReferenceQueryTreeSelectProvider(load, 50, tree);

      const page = await provider.loadRoots(req());

      expect(load).toHaveBeenCalledWith({ skip: 0, take: 50, search: undefined, columns: [{ name: 'Parent', includes: [null] }] });
      expect(page.nodes).toEqual([{ id: 'site', label: 'SITE', lazy: true }]);
    });

    it('loads a node\'s children by filtering the parent column on its id, paged', async () => {
      const load = vi.fn<ReferenceQueryPageLoader>().mockResolvedValue(result([row('bin-1', 0), row('bin-2', 0)], 120));
      const provider = new ReferenceQueryTreeSelectProvider(load, 50, tree);

      const page = await provider.loadChildren('locations/level-1', { ...req(), offset: 50 });

      expect(load).toHaveBeenCalledWith({ skip: 50, take: 50, search: undefined, columns: [{ name: 'Parent', includes: ['locations/level-1'] }] });
      expect(page.hasMore).toBe(true);
      expect(page.nodes.every(n => !n.lazy)).toBe(true);
    });

    it('searches flat across every level, without a parent filter', async () => {
      const load = vi.fn<ReferenceQueryPageLoader>().mockResolvedValue(result([row('bin-7', 0)]));
      const provider = new ReferenceQueryTreeSelectProvider(load, 50, tree);

      await provider.search('  bin  ', req());

      expect(load).toHaveBeenCalledWith({ skip: 0, take: 50, search: 'bin' });
    });

    it('marks every node lazy when no child-count column is declared', async () => {
      const load = vi.fn<ReferenceQueryPageLoader>().mockResolvedValue(result([row('a'), row('b')]));
      const provider = new ReferenceQueryTreeSelectProvider(load, 50, { parentAttribute: 'Parent' });

      const page = await provider.loadRoots(req());

      expect(page.nodes.every(n => n.lazy)).toBe(true);
    });
  });

  describe('referenceTreeNodeMayHaveChildren', () => {
    const tree = { parentAttribute: 'Parent', childCountAttribute: 'childcount' };

    it.each([
      [0, false],
      ['0', false],
      [false, false],
      [3, true],
      [true, true],
      [null, true],
      [undefined, true],
      ['n/a', true],
    ])('a child count of %s gives %s', (value, expected) => {
      expect(referenceTreeNodeMayHaveChildren(row('x', value), tree)).toBe(expected);
    });

    it('is true when the tree declares no child-count column', () => {
      expect(referenceTreeNodeMayHaveChildren(row('x', 0), { parentAttribute: 'Parent' })).toBe(true);
    });
  });
});
