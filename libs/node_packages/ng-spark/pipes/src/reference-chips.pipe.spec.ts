import { describe, expect, it } from 'vitest';
import { PersistentObject } from '@mintplayer/ng-spark/models';
import { ReferenceChipsPipe } from './reference-chips.pipe';

/** A multi-reference attribute rendered as chips: one per id, labelled from the server's breadcrumbs. */
describe('ReferenceChipsPipe', () => {
  const pipe = new ReferenceChipsPipe();
  const po = (attributes: unknown[]) => ({ id: 'Books/1', attributes }) as unknown as PersistentObject;

  it('labels each id from its breadcrumb, falling back to the id, and drops blank ids', () => {
    const item = po([
      { name: 'Other', value: ['ignored'] },
      { name: 'Tags', value: ['t/1', '', null, 't/2', 7], breadcrumbs: { 't/1': 'Fiction', 't/2': '' } },
    ]);

    expect(pipe.transform('Tags', item)).toEqual([
      { id: 't/1', label: 'Fiction' },
      { id: 't/2', label: 't/2' },
      { id: '7', label: '7' },
    ]);
  });

  it('uses the ids as labels when the attribute has no breadcrumbs', () => {
    expect(pipe.transform('Tags', po([{ name: 'Tags', value: ['t/1'] }]))).toEqual([{ id: 't/1', label: 't/1' }]);
  });

  it('returns no chips for a missing object, a missing attribute or a non-array value', () => {
    expect(pipe.transform('Tags', null)).toEqual([]);
    expect(pipe.transform('Tags', po([]))).toEqual([]);
    expect(pipe.transform('Tags', po([{ name: 'Tags', value: 't/1' }]))).toEqual([]);
  });
});
