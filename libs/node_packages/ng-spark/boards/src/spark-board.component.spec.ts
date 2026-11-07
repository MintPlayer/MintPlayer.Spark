import { Component, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
import { of } from 'rxjs';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { SparkLanguageService, SparkRenderPhase } from '@mintplayer/ng-spark/services';
import { SparkBoardComponent } from './spark-board.component';
import { SparkBoard, SparkBoardTile } from './spark-board.models';
import { SparkBoardsService } from './spark-boards.service';
import { SPARK_TILE_RENDERERS } from './spark-tile-renderers';

/**
 * The board's own logic (#530): loading by input or route, Customize/Cancel, the draft edits, Save with the
 * "apply to smaller screens" question, the 409 and failure messages, Reset, and link mode over the prerender
 * limit. The tile manager needs layout APIs jsdom lacks, so the template is swapped for an empty one; the board
 * is driven through its members, and the draft functions themselves are covered by board-draft.spec.
 */

const pos = (colStart: number, rowStart: number, colSpan = 1, rowSpan = 1) => ({ colStart, rowStart, colSpan, rowSpan });

function board(overrides: Partial<SparkBoard> = {}): SparkBoard {
  return {
    id: 'home',
    tiles: [
      { id: 'cars', kind: 'query', target: { query: 'cars' }, isDefault: true, title: { en: 'Cars' } },
      { id: 'car-count', kind: 'count', target: { query: 'cars' }, isDefault: true },
      { id: 'u-mine', kind: 'persistentObject', target: { entityType: 'Car', id: 'cars/1' }, isDefault: false, customTitle: 'Mine' },
    ],
    layouts: { '4': [{ id: 'cars', position: pos(1, 1, 2) }, { id: 'car-count', position: pos(3, 1) }, { id: 'u-mine', position: pos(4, 1) }] },
    customizedCounts: [],
    removed: [{ id: 'old', kind: 'count', target: { query: 'people' }, isDefault: true }],
    customized: true,
    canCustomize: true,
    changeVector: 'A:1',
    ...overrides,
  };
}

@Component({ selector: 'test-tile', template: '' })
class TestTileComponent {}

type Board = SparkBoardComponent & Record<string, any>;

const flush = async () => { for (let i = 0; i < 3; i++) await new Promise<void>((r) => setTimeout(r, 0)); };

async function render(options: {
  service?: Partial<Record<'get' | 'save' | 'reset', ReturnType<typeof vi.fn>>>;
  boardId?: string | null;
  routeBoardId?: string;
  serverMatching?: boolean;
} = {}) {
  const service = { get: vi.fn().mockResolvedValue(board()), save: vi.fn(), reset: vi.fn(), ...options.service };
  TestBed.configureTestingModule({
    providers: [
      { provide: SparkBoardsService, useValue: service },
      { provide: SparkLanguageService, useValue: { t: (k: string) => k, resolve: (v?: { en?: string }) => v?.en ?? '' } },
      { provide: SparkRenderPhase, useValue: { serverMatching: signal(options.serverMatching ?? false) } },
      { provide: SPARK_TILE_RENDERERS, useValue: { kind: 'query', component: TestTileComponent }, multi: true },
      ...(options.routeBoardId ? [{ provide: ActivatedRoute, useValue: { data: of({ boardId: options.routeBoardId }) } }] : []),
    ],
  });
  TestBed.overrideComponent(SparkBoardComponent, { set: { template: '', templateUrl: undefined, imports: [], styleUrl: undefined, styleUrls: [] } });
  const fixture = TestBed.createComponent(SparkBoardComponent);
  if (options.boardId !== undefined) fixture.componentRef.setInput('boardId', options.boardId);
  fixture.detectChanges();
  await flush();
  return { fixture, board: fixture.componentInstance as Board, service };
}

describe('SparkBoardComponent', () => {
  afterEach(() => vi.restoreAllMocks());

  it('loads the board named by the input, or else by the route, and shows a failed load', async () => {
    const byInput = await render({ boardId: 'home' });
    expect(byInput.service.get).toHaveBeenCalledWith('home');
    expect(byInput.board['board']()!.id).toBe('home');
    expect(byInput.board['items']().map((i: { label: string }) => i.label)).toEqual(['Cars', 'car-count', 'Mine']);
    expect(byInput.board['context']()).toEqual({ boardId: 'home', editing: false, reloadToken: 0 });

    TestBed.resetTestingModule();
    const byRoute = await render({ routeBoardId: 'fleet', service: { get: vi.fn().mockRejectedValue(new Error('404')) } });
    expect(byRoute.service.get).toHaveBeenCalledWith('fleet');
    expect(byRoute.board['loadFailed']()).toBe(true);

    TestBed.resetTestingModule();
    const none = await render();
    expect(none.service.get).not.toHaveBeenCalled();
  });

  it('customize starts a draft whose edits Cancel drops; a board that cannot be customized stays read-only', async () => {
    const { board: b } = await render({ boardId: 'home' });
    const before = b['items']()[0];

    b['customize']();
    expect(b['editing']()).toBe(true);
    expect(b['items']()[0]).not.toBe(before);
    expect(b['items']()[0].disableMove).toBe(false);

    b['remove']('car-count');
    b['onPick']({ id: 'old', kind: 'count', target: { query: 'people' } });
    b['onPick']({ kind: 'query', target: { query: 'people' }, title: { en: 'People' } });
    expect(b['tiles']().map((t: SparkBoardTile) => t.id)).toEqual(['cars', 'u-mine', 'old', expect.stringMatching(/^u-/)]);
    expect(b['pickerOpen']()).toBe(false);

    b['cancel']();
    expect(b['editing']()).toBe(false);
    expect(b['tiles']()).toHaveLength(3);

    TestBed.resetTestingModule();
    const locked = await render({ boardId: 'home', service: { get: vi.fn().mockResolvedValue(board({ canCustomize: false })) } });
    locked.board['customize']();
    expect(locked.board['editing']()).toBe(false);
  });

  it('renames a tile, and typing the title it already shows is no rename', async () => {
    const { board: b } = await render({ boardId: 'home' });
    b['customize']();
    const cars = b['tiles']()[0];

    b['startRename'](cars);
    expect(b['renaming']()).toEqual({ id: 'cars', title: 'Cars' });
    b['setRenameTitle']('Cars ');
    b['confirmRename']();
    expect(b['tiles']()[0].customTitle ?? '').toBe('');

    b['startRename'](b['tiles']()[0]);
    b['setRenameTitle']('Fleet');
    b['confirmRename']();
    expect(b['tiles']()[0].customTitle).toBe('Fleet');
    expect(b['renaming']()).toBeNull();

    b['setRenameTitle']('ignored');
    expect(b['renaming']()).toBeNull();
  });

  it('saves the draft and reloads; after a layout edit with narrower layouts it asks first', async () => {
    const saved = board({ changeVector: 'A:2' });
    const save = vi.fn().mockResolvedValue(saved);
    const { board: b } = await render({ boardId: 'home', service: { save } });

    b['customize']();
    b['save']();
    await flush();
    expect(save).toHaveBeenCalledWith('home', expect.objectContaining({ changeVector: 'A:1' }));
    expect(b['editing']()).toBe(false);
    expect(b['context']().reloadToken).toBe(1);

    b['customize']();
    b['onColumnCount']({ columnCount: 4 });
    b['onLayoutChange']({ columnCount: 4, layouts: b['layouts'](), staleCounts: [2] });
    b['save']();
    expect(b['askNarrower']()).toBe(true);
    await b['send'](true);
    expect(b['askNarrower']()).toBe(false);
    expect(save).toHaveBeenCalledTimes(2);

    b['resetWidth']();
    b['onLayoutChange']({ columnCount: 4, layouts: {}, staleCounts: [] });
    expect(b['draft']()).toBeNull();
  });

  it('names a conflict and any other failed save, and keeps the draft', async () => {
    const save = vi.fn()
      .mockRejectedValueOnce(new HttpErrorResponse({ status: 409 }))
      .mockRejectedValueOnce(new HttpErrorResponse({ status: 500 }));
    const { board: b } = await render({ boardId: 'home', service: { save } });
    b['customize']();

    await b['send'](false);
    expect(b['error']()).toBe('boards.spa.conflict');
    await b['send'](false);
    expect(b['error']()).toBe('boards.spa.saveFailed');
    expect(b['editing']()).toBe(true);
    expect(b['busy']()).toBe(false);
  });

  it('resets the stored layout after a confirmation, and names a failed reset', async () => {
    const reset = vi.fn().mockResolvedValueOnce(board({ customized: false })).mockRejectedValueOnce(new Error('down'));
    const confirm = vi.spyOn(window, 'confirm');
    const { board: b } = await render({ boardId: 'home', service: { reset } });

    confirm.mockReturnValueOnce(false);
    await b['reset']();
    expect(reset).not.toHaveBeenCalled();

    confirm.mockReturnValue(true);
    await b['reset']();
    expect(b['board']()!.customized).toBe(false);
    await b['reset']();
    expect(b['error']()).toBe('boards.spa.saveFailed');
  });

  it('links query, count and object tiles when a server render holds more tiles than it prerenders', async () => {
    const many = Array.from({ length: 8 }, (_, i) => ({ id: `t${i}`, kind: 'count', target: { query: 'cars' }, isDefault: true }));
    const { board: b } = await render({ boardId: 'home', serverMatching: true, service: { get: vi.fn().mockResolvedValue(board({ tiles: many })) } });

    expect(b['linkMode']()).toBe(true);
    expect(b['tileLink']({ id: 'q', kind: 'query', target: { query: 'cars' }, isDefault: true })).toEqual(['/query', 'cars']);
    expect(b['tileLink']({ id: 'q', kind: 'query', target: {}, isDefault: true })).toBeNull();
    expect(b['tileLink']({ id: 'p', kind: 'persistentObject', target: { entityType: 'Car', id: 'cars/1' }, isDefault: true })).toEqual(['/po', 'Car', 'cars/1']);
    expect(b['tileLink']({ id: 'p', kind: 'persistentObject', target: { entityType: 'Car' }, isDefault: true })).toBeNull();
    expect(b['tileLink']({ id: 'u', kind: 'programUnit', target: { programUnit: 'x' }, isDefault: true })).toBeNull();
    expect(b['renderer']({ id: 'q', kind: 'query', target: {}, isDefault: true })).toBe(TestTileComponent);
    expect(b['renderer']({ id: 'c', kind: 'count', target: {}, isDefault: true })).toBeNull();
    expect(b['asItem'](b['items']()[0]).tile.id).toBe('t0');
  });
});
