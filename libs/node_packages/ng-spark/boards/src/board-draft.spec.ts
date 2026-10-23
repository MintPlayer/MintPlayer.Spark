import { describe, expect, it } from 'vitest';
import { addTile, applyToNarrower, commitLayout, removeTile, renameTile, startDraft, toSaveRequest } from './board-draft';
import { SparkBoard } from './spark-board.models';

const pos = (colStart: number, rowStart: number, colSpan = 1, rowSpan = 1) => ({ colStart, rowStart, colSpan, rowSpan });

function board(overrides: Partial<SparkBoard> = {}): SparkBoard {
  return {
    id: 'home',
    tiles: [
      { id: 'cars', kind: 'query', target: { query: 'cars' }, isDefault: true },
      { id: 'car-count', kind: 'count', target: { query: 'cars' }, isDefault: true },
      { id: 'u-mine', kind: 'persistentObject', target: { entityType: 'Car', id: 'cars/1' }, isDefault: false, customTitle: 'Mine' },
    ],
    layouts: {
      '4': [{ id: 'cars', position: pos(1, 1, 2) }, { id: 'car-count', position: pos(3, 1) }, { id: 'u-mine', position: pos(4, 1) }],
      '2': [{ id: 'cars', position: pos(1, 1, 2) }, { id: 'car-count', position: pos(1, 2) }, { id: 'u-mine', position: pos(2, 2) }],
    },
    customizedCounts: [2],
    removed: [{ id: 'old', kind: 'count', target: { query: 'people' }, isDefault: true }],
    customized: true,
    canCustomize: true,
    changeVector: 'A:1',
    ...overrides,
  };
}

describe('board draft', () => {
  it('reads the server layouts with numeric keys', () => {
    const draft = startDraft(board());
    expect(Object.keys(draft.layouts).map(Number).sort()).toEqual([2, 4]);
    expect(draft.counts).toEqual([2]);
  });

  it('saves only the counts the user stores, and states removals, renames and own tiles whole', () => {
    const request = toSaveRequest(startDraft(board()), 'A:1');
    expect(Object.keys(request.layouts)).toEqual(['2']);
    expect(request.removed).toEqual(['old']);
    expect(request.titles).toEqual({ 'u-mine': 'Mine' });
    expect(request.tiles.map(t => t.id)).toEqual(['u-mine']);
    expect(request.changeVector).toBe('A:1');
  });

  it('a commit makes its count the user\'s', () => {
    const draft = startDraft(board());
    const layout = [{ id: 'cars', position: pos(1, 1, 4) }];
    const next = commitLayout(draft, { columnCount: 4, layouts: { ...draft.layouts, 4: layout } });
    expect(next.counts).toEqual([2, 4]);
    expect(toSaveRequest(next, null).layouts[4]).toEqual(layout);
  });

  it('apply to smaller screens drops the narrower stored layouts, so they leave the save', () => {
    let draft = startDraft(board());
    draft = commitLayout(draft, { columnCount: 4, layouts: draft.layouts });
    draft = applyToNarrower(draft, 4);
    expect(Object.keys(toSaveRequest(draft, null).layouts)).toEqual(['4']);
  });

  it('a count reset this session ("Reset this width") leaves the save', () => {
    const draft = startDraft(board());
    const { 2: _dropped, ...rest } = draft.layouts;
    const next = commitLayout(draft, { columnCount: 2, layouts: rest });
    expect(toSaveRequest(next, null).layouts).toEqual({});
  });

  it('removing a default moves it to removed and out of every layout; removing a user tile drops it', () => {
    let draft = removeTile(startDraft(board()), 'car-count');
    expect(draft.removed.map(t => t.id)).toEqual(['old', 'car-count']);
    expect(Object.values(draft.layouts).flat().some(e => e.id === 'car-count')).toBe(false);
    draft = removeTile(draft, 'u-mine');
    expect(draft.removed.map(t => t.id)).toEqual(['old', 'car-count']);
    const request = toSaveRequest(draft, null);
    expect(request.tiles).toEqual([]);
    expect(request.removed).toEqual(['old', 'car-count']);
    expect(request.titles).toEqual({});
  });

  it('adding a removed default restores it instead of adding a user tile', () => {
    const draft = addTile(startDraft(board()), { id: 'old', kind: 'count', target: { query: 'people' } });
    expect(draft.tiles.at(-1)).toMatchObject({ id: 'old', isDefault: true });
    expect(draft.removed).toEqual([]);
    expect(toSaveRequest(draft, null).removed).toEqual([]);
  });

  it('a new tile gets a fresh u- id and no position', () => {
    const draft = addTile(startDraft(board()), { kind: 'count', target: { query: 'people' } });
    const added = draft.tiles.at(-1)!;
    expect(added.id).toMatch(/^u-[a-z0-9]+$/);
    expect(added.isDefault).toBe(false);
    expect(toSaveRequest(draft, null).tiles.map(t => t.id)).toContain(added.id);
    expect(Object.values(draft.layouts).flat().some(e => e.id === added.id)).toBe(false);
  });

  it('renaming trims, and an empty title drops the rename', () => {
    let draft = renameTile(startDraft(board()), 'cars', '  All cars ');
    expect(toSaveRequest(draft, null).titles['cars']).toBe('All cars');
    draft = renameTile(draft, 'cars', '   ');
    expect(toSaveRequest(draft, null).titles['cars']).toBeUndefined();
  });

  it('never sends a position for a tile that is not on the board', () => {
    const b = board();
    b.layouts['2'] = [...b.layouts['2'], { id: 'ghost', position: pos(1, 3) }];
    expect(toSaveRequest(startDraft(b), null).layouts[2].map(e => e.id)).not.toContain('ghost');
  });
});
