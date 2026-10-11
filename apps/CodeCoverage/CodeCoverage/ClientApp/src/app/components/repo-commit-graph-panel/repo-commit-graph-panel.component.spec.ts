import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { CommitGraphPage, CommitGraphRow } from '../../services/browse.service';
import { BrowseServiceStub, createBrowseStub, provideBrowseStub, settle } from '../../../testing/test-utils';
import { GRAPH_PAGE_SIZE, POLL_FIRST_MS, RepoCommitGraphPanelComponent } from './repo-commit-graph-panel.component';

function row(sha: string, parents: string[] = []): CommitGraphRow {
  return {
    sha, parents, subject: `commit ${sha}`, authorName: 'Test Author', date: '2026-01-01T00:00:00Z',
    lane: 0, colour: 0, up: [], down: parents.length ? [[0, 0]] : [], refs: [], coverage: null, avatarKey: null,
  };
}

function page(overrides: Partial<CommitGraphPage> = {}): CommitGraphPage {
  return {
    state: 'ready', reason: null, snapshotId: 'snap-1', totalRows: 2, from: 0, laneCount: 1, top: [],
    refs: [{ name: 'refs/heads/main', kind: 'head', sha: 'aaaaaaa1' }, { name: 'refs/tags/v1', kind: 'tag', sha: 'aaaaaaa1' }],
    rows: [row('aaaaaaa1', ['bbbbbbb2']), row('bbbbbbb2')],
    ...overrides,
  };
}

const empty = (state: CommitGraphPage['state'], reason: string | null = null): CommitGraphPage =>
  page({ state, reason, snapshotId: null, totalRows: 0, rows: [], refs: [] });

describe('RepoCommitGraphPanelComponent', () => {
  let fixture: ComponentFixture<RepoCommitGraphPanelComponent>;
  let browse: BrowseServiceStub;

  afterEach(() => vi.useRealTimers());

  async function create(getCommitGraph: (...args: any[]) => Promise<CommitGraphPage>, flush = () => settle(fixture)) {
    browse = createBrowseStub({
      getCommitGraph,
      getRepo: () => Promise.resolve({ defaultBranch: 'main', latestCoverageSha: 'aaaaaaa1' }),
      avatarUrl: (key: string) => `/api/browse/avatar/${key}`,
    });
    TestBed.configureTestingModule({
      imports: [RepoCommitGraphPanelComponent],
      providers: [provideZonelessChangeDetection(), provideRouter([]), provideBrowseStub(browse)],
    });
    fixture = TestBed.createComponent(RepoCommitGraphPanelComponent);
    fixture.componentRef.setInput('provider', 'github');
    fixture.componentRef.setInput('owner', 'acme');
    fixture.componentRef.setInput('name', 'widgets');
    await flush();
  }

  const text = () => (fixture.nativeElement as HTMLElement).textContent ?? '';
  const panel = () => fixture.componentInstance;

  it('loads the first page of all refs and pages on from the same snapshot', async () => {
    const first = page({ totalRows: 250, rows: Array.from({ length: GRAPH_PAGE_SIZE }, (_, i) => row(`s${i}`, [`s${i + 1}`])) });
    const second = page({ from: 200, totalRows: 250, top: [[0, 0]], refs: [], rows: Array.from({ length: 50 }, (_, i) => row(`s${200 + i}`)) });
    await create((_p, _o, _n, q) => Promise.resolve(q.from ? second : first));

    expect(browse.getCommitGraph).toHaveBeenCalledWith('github', 'acme', 'widgets', expect.objectContaining({ count: GRAPH_PAGE_SIZE }));
    expect(panel().state()).toBe('ready');
    expect(fixture.nativeElement.querySelector('bs-datatable')).not.toBeNull();
    expect(panel().branches().map((b) => b.name)).toEqual(['refs/heads/main']);

    // Page 1 is served from what the probe already loaded: no second request.
    const fetch = panel().fetchFn()!;
    const p1 = await fetch({ page: 1, perPage: GRAPH_PAGE_SIZE, sortColumns: [] });
    expect(p1.data.length).toBe(200);
    expect(p1.totalRecords).toBe(250);
    expect(browse.getCommitGraph).toHaveBeenCalledTimes(1);

    const p2 = await fetch({ page: 2, perPage: GRAPH_PAGE_SIZE, sortColumns: [] });
    expect(browse.getCommitGraph).toHaveBeenLastCalledWith('github', 'acme', 'widgets',
      expect.objectContaining({ from: 200, count: GRAPH_PAGE_SIZE, snapshot: 'snap-1' }));
    expect(p2.data.map((v) => v.index)).toEqual(Array.from({ length: 50 }, (_, i) => 200 + i));
    // The pass-through above page 2 comes from its `top` header.
    expect(p2.data[0].row.sha).toBe('s200');
  });

  it('polls while the mirror syncs, backing off, until the graph is ready', async () => {
    vi.useFakeTimers();
    const responses = [empty('syncing'), empty('syncing'), page()];
    const flush = async () => {
      for (let i = 0; i < 6; i++) {
        await vi.advanceTimersByTimeAsync(0);
        fixture.detectChanges();
      }
    };
    await create(() => Promise.resolve(responses.shift()!), flush);

    expect(panel().state()).toBe('syncing');
    expect(text()).toContain('Mirroring the repository');

    await vi.advanceTimersByTimeAsync(POLL_FIRST_MS);
    await flush();
    expect(browse.getCommitGraph).toHaveBeenCalledTimes(2);
    expect(panel().state()).toBe('syncing');

    // The second wait is twice the first.
    await vi.advanceTimersByTimeAsync(POLL_FIRST_MS);
    await flush();
    expect(browse.getCommitGraph).toHaveBeenCalledTimes(2);
    await vi.advanceTimersByTimeAsync(POLL_FIRST_MS);
    await flush();
    expect(browse.getCommitGraph).toHaveBeenCalledTimes(3);
    expect(panel().state()).toBe('ready');
  });

  it('explains an unavailable graph by its reason', async () => {
    await create(() => Promise.resolve(empty('unavailable', 'too-large')));
    expect(panel().state()).toBe('unavailable');
    expect(text()).toContain('too large to mirror');
  });

  it('falls back to a generic message for an unknown reason or a failed request', async () => {
    await create(() => Promise.reject(new Error('network')));
    expect(panel().state()).toBe('unavailable');
    expect(text()).toContain('could not be loaded');
  });

  it('says so when the repository has no commits', async () => {
    await create(() => Promise.resolve(page({ totalRows: 0, rows: [] })));
    expect(panel().state()).toBe('empty');
    expect(text()).toContain('no commits yet');
  });

  it('restarts at the first visible commit when the snapshot goes stale', async () => {
    const calls: any[] = [];
    await create((_p, _o, _n, q) => {
      calls.push(q);
      if (q.from) return Promise.resolve(empty('stale'));
      return Promise.resolve(page({ totalRows: 400, snapshotId: q.at ? 'snap-2' : 'snap-1' }));
    });

    await expect(panel().fetchFn()!({ page: 2, perPage: GRAPH_PAGE_SIZE, sortColumns: [] })).rejects.toThrow('stale');
    await settle(fixture);
    // No scroll container in jsdom: the first visible row is row 0.
    const restart = calls.findIndex((q) => q.at);
    expect(calls[restart]).toEqual(expect.objectContaining({ at: 'aaaaaaa1' }));
    expect(calls[restart].snapshot).toBeUndefined();
    expect(panel().state()).toBe('ready');
  });

  it('jumps to a SHA search in the full graph and reports a SHA it does not know', async () => {
    const calls: any[] = [];
    await create((_p, _o, _n, q) => {
      calls.push(q);
      if (q.at === 'ccccccc') return Promise.resolve(empty('not-found'));
      return Promise.resolve(page());
    });

    panel().searchText.set('bbbbbbb2');
    panel().appliedSearch.set('bbbbbbb2');
    await settle(fixture);
    expect(calls.at(-1)).toEqual(expect.objectContaining({ at: 'bbbbbbb2' }));
    expect(calls.at(-1).grep).toBeUndefined();
    expect(panel().cursor()).toBe(1);
    expect(panel().listMode()).toBe(false);

    // Not a known commit: searched as message text instead.
    panel().appliedSearch.set('ccccccc');
    await settle(fixture);
    expect(calls.at(-1)).toEqual(expect.objectContaining({ grep: 'ccccccc' }));
    expect(panel().listMode()).toBe(true);
  });

  it('searches by author, and a hit opens in the graph', async () => {
    const calls: any[] = [];
    await create((_p, _o, _n, q) => {
      calls.push(q);
      return Promise.resolve(page());
    });

    panel().setSearchMode('author');
    panel().appliedSearch.set('Test Author');
    await settle(fixture);
    expect(calls.at(-1)).toEqual(expect.objectContaining({ author: 'Test Author' }));
    expect(panel().listMode()).toBe(true);

    const hit = (await panel().fetchFn()!({ page: 1, perPage: GRAPH_PAGE_SIZE, sortColumns: [] })).data[1];
    panel().open(hit);
    await settle(fixture);
    expect(calls.at(-1)).toEqual(expect.objectContaining({ at: 'bbbbbbb2' }));
    expect(calls.at(-1).author).toBeUndefined();
    expect(panel().listMode()).toBe(false);
  });

  it('narrows to one branch and to first parents', async () => {
    const calls: any[] = [];
    await create((_p, _o, _n, q) => {
      calls.push(q);
      return Promise.resolve(page());
    });
    panel().selectRef('refs/heads/main');
    await settle(fixture);
    expect(calls.at(-1)).toEqual(expect.objectContaining({ ref: 'refs/heads/main' }));
    panel().toggleFirstParent();
    await settle(fixture);
    expect(calls.at(-1)).toEqual(expect.objectContaining({ ref: 'refs/heads/main', firstParent: true }));
  });

  it('opens the commit page on a row click and moves a keyboard cursor', async () => {
    const covered = { linesCovered: 1, linesCoverable: 2, percent: 50, deltaVsParent: null, completeness: 'Complete' };
    await create(() => Promise.resolve(page({ rows: [{ ...row('aaaaaaa1', ['bbbbbbb2']), coverage: covered }, row('bbbbbbb2')] })));
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);

    const keys = (key: string) => panel().onKeydown(new KeyboardEvent('keydown', { key }));
    keys('ArrowDown');
    expect(panel().cursor()).toBe(0);
    keys('ArrowDown');
    keys('ArrowDown');
    expect(panel().cursor()).toBe(1);
    // The cursor's first-parent path is highlighted.
    expect([...panel().highlight().keys()]).toEqual([1]);
    // No upload ever reached this commit: there is no commit page to open.
    keys('Enter');
    expect(navigate).not.toHaveBeenCalled();
    expect(panel().notice()).toContain('bbbbbbb');
    keys('ArrowUp');
    expect([...panel().highlight().keys()]).toEqual([0, 1]);
    keys('Enter');
    expect(navigate).toHaveBeenCalledWith(['/', 'github', 'r', 'acme', 'widgets', 'c', 'aaaaaaa1']);
  });

  it('draws a coverage sparkline along the default branch, then along the selected one', async () => {
    const cov = (percent: number) => ({ linesCovered: 1, linesCoverable: 2, percent, deltaVsParent: null, completeness: 'Complete' });
    const rows = [
      { ...row('aaaaaaa1', ['bbbbbbb2']), coverage: cov(80) },
      { ...row('ccccccc3', ['bbbbbbb2']), coverage: cov(20) },
      { ...row('bbbbbbb2'), coverage: cov(60) },
    ];
    const refs = [{ name: 'refs/heads/main', kind: 'head', sha: 'aaaaaaa1' }, { name: 'refs/heads/topic', kind: 'head', sha: 'ccccccc3' }];
    await create(() => Promise.resolve(page({ totalRows: 3, rows, refs })));

    expect(panel().sparkline()).toEqual([60, 80]);
    expect(fixture.nativeElement.querySelector('spark-trend-sparkline')).not.toBeNull();
    panel().selectRef('refs/heads/topic');
    await settle(fixture);
    expect(panel().sparkline()).toEqual([60, 20]);
    expect(panel().sparklineOptions()['label']).toContain('topic');
  });

  it('lists the commits that dropped coverage, and a drop opens in the graph', async () => {
    const calls: any[] = [];
    await create((_p, _o, _n, q) => {
      calls.push(q);
      return Promise.resolve(page());
    });
    const drop = { sha: 'bbbbbbb2', branch: 'main', message: 'Remove tests', date: '2026-01-01T00:00:00Z', percent: 40, deltaVsParent: -2.5 };
    browse.getCoverageDrops.mockImplementation(() => Promise.resolve([drop]));

    panel().selectRef('refs/heads/main');
    await settle(fixture);
    panel().toggleDrops();
    await settle(fixture);
    expect(browse.getCoverageDrops).toHaveBeenLastCalledWith('github', 'acme', 'widgets', 1, 'main');
    const button = fixture.nativeElement.querySelector('button.cg-drop') as HTMLButtonElement;
    expect(button.getAttribute('aria-label')).toContain('coverage fell 2.5 points');
    expect(fixture.nativeElement.querySelector('bs-datatable')).toBeNull();

    // A half-typed threshold keeps the last one; a real one reloads.
    panel().setDropThreshold('');
    panel().setDropThreshold('5');
    await settle(fixture);
    expect(browse.getCoverageDrops).toHaveBeenLastCalledWith('github', 'acme', 'widgets', 5, 'main');

    browse.getCoverageDrops.mockImplementation(() => Promise.resolve([]));
    panel().setDropThreshold(50);
    await settle(fixture);
    expect(text()).toContain('No commit dropped coverage by more than 50 points');

    browse.getCoverageDrops.mockImplementation(() => Promise.resolve([drop]));
    panel().setDropThreshold(1);
    await settle(fixture);
    (fixture.nativeElement.querySelector('button.cg-drop') as HTMLButtonElement).click();
    await settle(fixture);
    expect(panel().dropMode()).toBe(false);
    expect(calls.at(-1)).toEqual(expect.objectContaining({ ref: 'refs/heads/main', at: 'bbbbbbb2' }));
  });

  it('says the drop list could not be loaded', async () => {
    await create(() => Promise.resolve(page()));
    browse.getCoverageDrops.mockImplementation(() => Promise.reject(new Error('boom')));
    panel().toggleDrops();
    await settle(fixture);
    expect(panel().dropsFailed()).toBe(true);
    expect(text()).toContain('could not be loaded');
  });

  it('gives every row a labelled button and keeps the graph out of the accessibility tree', async () => {
    const covered = { linesCovered: 1, linesCoverable: 2, percent: 50, deltaVsParent: -1.5, completeness: 'Complete' };
    await create(() => Promise.resolve(page({ rows: [{ ...row('aaaaaaa1', ['bbbbbbb2']), coverage: covered }, row('bbbbbbb2')] })));
    const views = (await panel().fetchFn()!({ page: 1, perPage: GRAPH_PAGE_SIZE, sortColumns: [] })).data as any[];
    expect(views[0].ui.label).toBe('Open aaaaaaa: 50.0% covered, down 1.5 points; commit aaaaaaa1');
    expect(views[1].ui.label).toBe('Open bbbbbbb: no coverage report; commit bbbbbbb2');

    // A row button opens its commit without the row click firing a second time.
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const event = new MouseEvent('click');
    const stop = vi.spyOn(event, 'stopPropagation');
    panel().openFromButton(event, views[0]);
    expect(stop).toHaveBeenCalled();
    expect(navigate).toHaveBeenCalledTimes(1);

    for (const control of fixture.nativeElement.querySelectorAll('.cg-toolbar button, .cg-toolbar input, .cg-toolbar bs-select')) {
      const named = control.getAttribute('aria-label') || control.textContent?.trim();
      expect(named, control.outerHTML).toBeTruthy();
    }
    for (const svg of fixture.nativeElement.querySelectorAll('.cg-graph svg')) {
      expect(svg.getAttribute('aria-hidden')).toBe('true');
    }
  });

  it('encodes coverage on the node', async () => {
    await create(() => Promise.resolve(page()));
    const view = (coverage: CommitGraphRow['coverage']) =>
      ({ ...panel().fetchFn, row: { ...row('x'), coverage }, cx: 7, cy: 16, colour: 0, index: 0, segments: [], width: 1, isMerge: false });

    expect(panel().nodeClass(view(null))).toBe('cg-node cg-none');
    const pending = { linesCovered: null, linesCoverable: null, percent: null, deltaVsParent: null, completeness: null };
    expect(panel().nodeClass(view(pending))).toBe('cg-node cg-pending');
    const half = { linesCovered: 5, linesCoverable: 10, percent: 50, deltaVsParent: -1, completeness: 'Complete' };
    expect(panel().nodeClass(view(half))).toBe('cg-node');
    expect(panel().pie(view(half))).toMatch(/^M7 16L7 12A4 4 0 0 1 /);
    expect(panel().pie(view({ ...half, completeness: 'Partial' }))).toBeNull();
    expect(panel().initials('Test Author')).toBe('TA');
    expect(panel().initials('')).toBe('?');
  });
});
