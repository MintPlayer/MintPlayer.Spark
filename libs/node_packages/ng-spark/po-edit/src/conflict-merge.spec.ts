import { describe, expect, it } from 'vitest';
import { AS_DETAIL_ROW_KEY, AS_DETAIL_SELF_BREADCRUMB_KEY, EntityAttributeDefinition, EntityType } from '@mintplayer/ng-spark/models';
import { MergeSchema, mergeThreeWay } from './conflict-merge';

function attr(name: string, extra: Partial<EntityAttributeDefinition> = {}): EntityAttributeDefinition {
  return { id: name, name, dataType: 'string', isRequired: false, isVisible: true, isReadOnly: false, order: 0, rules: [], ...extra } as EntityAttributeDefinition;
}

const lyricType: EntityType = {
  id: 't-lyric', name: 'Lyric', clrType: 'Test.Lyric',
  attributes: [attr('Language'), attr('Text'), attr('Checked', { isReadOnly: true })],
} as unknown as EntityType;

const schema: MergeSchema = {
  attributes: [
    attr('Title'),
    attr('Released'),
    attr('Lyrics', { dataType: 'AsDetail', isArray: true, asDetailType: 'Test.Lyric' }),
  ],
  resolve: clr => (clr === 'Test.Lyric' ? lyricType : undefined),
};

function row(key: string | undefined, values: Record<string, any>, breadcrumb?: string): Record<string, any> {
  const r: Record<string, any> = { Language: 'en', Text: '', Checked: false, ...values };
  if (key !== undefined) r[AS_DETAIL_ROW_KEY] = key;
  if (breadcrumb !== undefined) r[AS_DETAIL_SELF_BREADCRUMB_KEY] = breadcrumb;
  return r;
}

function form(values: Record<string, any> = {}): Record<string, any> {
  return { Title: 'Song', Released: '2020', Lyrics: [], ...values };
}

const keys = (rows: Record<string, any>[]) => rows.map(r => r[AS_DETAIL_ROW_KEY] ?? '(new)');

describe('mergeThreeWay — scalar attributes', () => {
  it('keeps a value only I changed', () => {
    const r = mergeThreeWay(form(), form({ Title: 'Mine' }), form(), schema);
    expect(r.merged['Title']).toBe('Mine');
    expect(r.conflicts).toEqual([]);
    expect(r.theirChanges).toEqual([]);
  });

  it('takes a value only they changed, and reports it', () => {
    const r = mergeThreeWay(form(), form(), form({ Released: '2021' }), schema);
    expect(r.merged['Released']).toBe('2021');
    expect(r.conflicts).toEqual([]);
    expect(r.theirChanges.map(c => c.path)).toEqual(['Released']);
  });

  it('merges disjoint edits of different attributes', () => {
    const r = mergeThreeWay(form(), form({ Title: 'Mine' }), form({ Released: '2021' }), schema);
    expect(r.merged).toMatchObject({ Title: 'Mine', Released: '2021' });
    expect(r.conflicts).toEqual([]);
  });

  it('is no conflict when both changed it to the same value', () => {
    const r = mergeThreeWay(form(), form({ Title: 'Same' }), form({ Title: 'Same' }), schema);
    expect(r.merged['Title']).toBe('Same');
    expect(r.conflicts).toEqual([]);
  });

  it('is a conflict when both changed it differently, resolved to theirs until chosen', () => {
    const base = form(), mine = form({ Title: 'Mine' }), theirs = form({ Title: 'Theirs' });
    const r = mergeThreeWay(base, mine, theirs, schema);
    expect(r.conflicts).toHaveLength(1);
    expect(r.conflicts[0]).toMatchObject({ path: 'Title', kind: 'value', group: '', base: 'Song', mine: 'Mine', theirs: 'Theirs' });
    expect(r.merged['Title']).toBe('Theirs');

    expect(mergeThreeWay(base, mine, theirs, schema, { Title: 'mine' }).merged['Title']).toBe('Mine');
    expect(mergeThreeWay(base, mine, theirs, schema, { Title: 'theirs' }).merged['Title']).toBe('Theirs');
  });

  it('treats a read-only attribute as theirs, without a conflict', () => {
    const readOnly: MergeSchema = { ...schema, attributes: [attr('Title', { isReadOnly: true })] };
    const r = mergeThreeWay({ Title: 'a' }, { Title: 'b' }, { Title: 'c' }, readOnly);
    expect(r.merged['Title']).toBe('c');
    expect(r.conflicts).toEqual([]);
  });
});

describe('mergeThreeWay — AsDetail rows', () => {
  const ko = row('ko/Kore', { Language: 'ko', Text: 'annyeong' }, 'Korean');
  const en = row('en/Latn', { Language: 'en', Text: 'hello' });

  it('keeps a row only I added (a new row has no key yet)', () => {
    const added = row(undefined, { Text: 'new' });
    const r = mergeThreeWay(form({ Lyrics: [ko] }), form({ Lyrics: [ko, added] }), form({ Lyrics: [ko] }), schema);
    expect(r.merged['Lyrics']).toEqual([ko, added]);
    expect(r.conflicts).toEqual([]);
  });

  it('takes a row only they added, and reports it', () => {
    const r = mergeThreeWay(form({ Lyrics: [ko] }), form({ Lyrics: [ko] }), form({ Lyrics: [ko, en] }), schema);
    expect(keys(r.merged['Lyrics'])).toEqual(['ko/Kore', 'en/Latn']);
    expect(r.theirChanges.map(c => c.path)).toEqual(['Lyrics[en/Latn]']);
  });

  it('is no conflict when both added the same key with the same content', () => {
    // The breadcrumb differs between the two reads; it is not content.
    const mineEn = row('en/Latn', { Text: 'hello' }, 'x');
    const r = mergeThreeWay(form(), form({ Lyrics: [mineEn] }), form({ Lyrics: [en] }), schema);
    expect(r.conflicts).toEqual([]);
    expect(r.merged['Lyrics']).toEqual([en]);
  });

  it('is a row conflict when both added the same key with different content', () => {
    const mineEn = row('en/Latn', { Text: 'hi' });
    const r = mergeThreeWay(form(), form({ Lyrics: [mineEn] }), form({ Lyrics: [en] }), schema);
    expect(r.conflicts).toHaveLength(1);
    expect(r.conflicts[0]).toMatchObject({ path: 'Lyrics[en/Latn]', kind: 'row', base: undefined, mine: mineEn, theirs: en });
    expect(mergeThreeWay(form(), form({ Lyrics: [mineEn] }), form({ Lyrics: [en] }), schema, { 'Lyrics[en/Latn]': 'mine' }).merged['Lyrics']).toEqual([mineEn]);
  });

  it('removes a row only I removed', () => {
    const r = mergeThreeWay(form({ Lyrics: [ko, en] }), form({ Lyrics: [ko] }), form({ Lyrics: [ko, en] }), schema);
    expect(keys(r.merged['Lyrics'])).toEqual(['ko/Kore']);
    expect(r.conflicts).toEqual([]);
  });

  it('removes a row only they removed, and reports it', () => {
    const r = mergeThreeWay(form({ Lyrics: [ko, en] }), form({ Lyrics: [ko, en] }), form({ Lyrics: [ko] }), schema);
    expect(keys(r.merged['Lyrics'])).toEqual(['ko/Kore']);
    expect(r.conflicts).toEqual([]);
    expect(r.theirChanges.map(c => c.path)).toEqual(['Lyrics[en/Latn]']);
  });

  it('is a row conflict when I removed a row they edited', () => {
    const theirsKo = { ...ko, Text: 'edited' };
    const base = form({ Lyrics: [ko] }), mine = form({ Lyrics: [] }), theirs = form({ Lyrics: [theirsKo] });
    const r = mergeThreeWay(base, mine, theirs, schema);
    expect(r.conflicts).toHaveLength(1);
    expect(r.conflicts[0]).toMatchObject({ path: 'Lyrics[ko/Kore]', kind: 'row', mine: undefined, theirs: theirsKo, rowLabel: 'Korean' });
    expect(r.merged['Lyrics']).toEqual([theirsKo]);
    expect(mergeThreeWay(base, mine, theirs, schema, { 'Lyrics[ko/Kore]': 'mine' }).merged['Lyrics']).toEqual([]);
  });

  it('is a row conflict when they removed a row I edited', () => {
    const mineKo = { ...ko, Text: 'edited' };
    const base = form({ Lyrics: [ko] }), mine = form({ Lyrics: [mineKo] }), theirs = form({ Lyrics: [] });
    const r = mergeThreeWay(base, mine, theirs, schema);
    expect(r.conflicts).toHaveLength(1);
    expect(r.conflicts[0]).toMatchObject({ path: 'Lyrics[ko/Kore]', kind: 'row', mine: mineKo, theirs: undefined });
    expect(r.merged['Lyrics']).toEqual([]);
    expect(mergeThreeWay(base, mine, theirs, schema, { 'Lyrics[ko/Kore]': 'mine' }).merged['Lyrics']).toEqual([mineKo]);
  });

  it('merges a row both edited on different attributes', () => {
    const mineKo = { ...ko, Text: 'mine text' };
    const theirsKo = { ...ko, Language: 'ko-KR' };
    const r = mergeThreeWay(form({ Lyrics: [ko] }), form({ Lyrics: [mineKo] }), form({ Lyrics: [theirsKo] }), schema);
    expect(r.conflicts).toEqual([]);
    expect(r.merged['Lyrics'][0]).toMatchObject({ Language: 'ko-KR', Text: 'mine text', [AS_DETAIL_ROW_KEY]: 'ko/Kore' });
    expect(r.theirChanges.map(c => c.path)).toEqual(['Lyrics[ko/Kore].Language']);
  });

  it('is an attribute conflict inside the row when both edited the same attribute', () => {
    const mineKo = { ...ko, Text: 'mine' };
    const theirsKo = { ...ko, Text: 'theirs' };
    const r = mergeThreeWay(form({ Lyrics: [ko] }), form({ Lyrics: [mineKo] }), form({ Lyrics: [theirsKo] }), schema);
    expect(r.conflicts).toHaveLength(1);
    expect(r.conflicts[0]).toMatchObject({
      path: 'Lyrics[ko/Kore].Text', kind: 'value', group: 'Lyrics[ko/Kore]', rowLabel: 'Korean',
      mine: 'mine', theirs: 'theirs',
    });
    expect(r.conflicts[0].rootAttribute.name).toBe('Lyrics');
    const chosen = mergeThreeWay(form({ Lyrics: [ko] }), form({ Lyrics: [mineKo] }), form({ Lyrics: [theirsKo] }), schema, { 'Lyrics[ko/Kore].Text': 'mine' });
    expect(chosen.merged['Lyrics'][0].Text).toBe('mine');
  });

  it('takes a read-only row attribute from theirs, without a conflict', () => {
    const mineKo = { ...ko, Checked: true };
    const theirsKo = { ...ko, Checked: false, Text: 'theirs' };
    const r = mergeThreeWay(form({ Lyrics: [ko] }), form({ Lyrics: [{ ...mineKo, Text: 'mine' }] }), form({ Lyrics: [theirsKo] }), schema);
    expect(r.conflicts.map(c => c.path)).toEqual(['Lyrics[ko/Kore].Text']);
    expect(r.merged['Lyrics'][0].Checked).toBe(false);
  });

  it('orders rows as theirs, then the rows only I have', () => {
    const a = row('a', { Text: 'a' }), b = row('b', { Text: 'b' }), c = row('c', { Text: 'c' });
    const mineNew = row(undefined, { Text: 'new' });
    const mineKeyed = row('m', { Text: 'm' });
    // Base a,b. They reorder to b,a and add c. I add a keyed row and a new one.
    const r = mergeThreeWay(
      form({ Lyrics: [a, b] }),
      form({ Lyrics: [a, mineKeyed, b, mineNew] }),
      form({ Lyrics: [b, a, c] }),
      schema,
    );
    expect(keys(r.merged['Lyrics'])).toEqual(['b', 'a', 'c', 'm', '(new)']);
  });

  it('ignores per-read fields when deciding whether a row changed', () => {
    const reread = row('ko/Kore', { Language: 'ko', Text: 'annyeong' }, 'Korean (re-read)');
    const r = mergeThreeWay(form({ Lyrics: [ko] }), form({ Lyrics: [ko], Title: 'Mine' }), form({ Lyrics: [reread] }), schema);
    expect(r.conflicts).toEqual([]);
    expect(r.theirChanges).toEqual([]);
    // Theirs is the frame, so the fresh per-read fields are the ones kept.
    expect(r.merged['Lyrics'][0][AS_DETAIL_SELF_BREADCRUMB_KEY]).toBe('Korean (re-read)');
  });
});
