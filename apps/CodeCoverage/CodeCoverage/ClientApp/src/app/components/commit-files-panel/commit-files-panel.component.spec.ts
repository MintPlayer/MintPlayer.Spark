import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter, Router } from '@angular/router';
import { BsHierarchyChartComponent } from '@mintplayer/ng-bootstrap/charts/hierarchy';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { CommitDetail, CoverageHierarchyNode, TreeResponse } from '../../services/browse.service';
import {
  ActivatedRouteStub, BrowseServiceStub, createBrowseStub, provideActivatedRouteStub, provideBrowseStub, settle,
  StubHierarchyChart,
} from '../../../testing/test-utils';
import { CommitFilesPanelComponent } from './commit-files-panel.component';

function tree(entries: TreeResponse['entries'] = [], extra: Partial<TreeResponse> = {}): TreeResponse {
  return { buildId: 'b1', entries, unmatchedFiles: [], unmatchedTotal: 0, ...extra };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

const hierarchy: CoverageHierarchyNode = { id: '/', name: 'root', children: [{ id: 'src', name: 'src', value: 10 }] };

describe('CommitFilesPanelComponent', () => {
  let fixture: ComponentFixture<CommitFilesPanelComponent>;
  let component: CommitFilesPanelComponent;
  let browse: BrowseServiceStub;
  let route: ActivatedRouteStub;
  let navigate: ReturnType<typeof vi.fn>;

  async function create(opts: { browse?: Parameters<typeof createBrowseStub>[0]; queryParams?: Record<string, string> } = {}) {
    browse = createBrowseStub({
      getTree: () => Promise.resolve(tree([
        { name: 'src', path: 'src', isFile: false, linesCovered: 5, linesCoverable: 10 },
        { name: 'a.ts', path: 'a.ts', isFile: true, linesCovered: 1, linesCoverable: 2, origin: 'Measured' },
      ])),
      getHierarchy: () => Promise.resolve(hierarchy),
      getCommit: () => Promise.resolve({ id: 'c1', sha: 'abc', builds: [] } as CommitDetail),
      ...opts.browse,
    });
    route = new ActivatedRouteStub({ queryParams: opts.queryParams ?? {} });

    TestBed.configureTestingModule({
      imports: [CommitFilesPanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideNoopAnimations(),
        provideRouter([]),
        provideBrowseStub(browse),
        provideActivatedRouteStub(route),
      ],
    });
    TestBed.overrideComponent(CommitFilesPanelComponent, {
      remove: { imports: [BsHierarchyChartComponent] },
      add: { imports: [StubHierarchyChart] },
    });

    navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true) as never;
    fixture = TestBed.createComponent(CommitFilesPanelComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('provider', 'github');
    fixture.componentRef.setInput('owner', 'acme');
    fixture.componentRef.setInput('name', 'widgets');
    fixture.componentRef.setInput('sha', 'abc1234567');
    await settle(fixture);
  }

  const text = () => (fixture.nativeElement as HTMLElement).textContent ?? '';
  const chart = () => fixture.debugElement.query(By.directive(StubHierarchyChart));

  afterEach(() => vi.restoreAllMocks());

  describe('rejectionText', () => {
    beforeEach(() => create());

    it.each([
      ['empty', 'the file was empty'],
      ['unrecognizedFormat', 'the format was not recognised (supported: Cobertura, JaCoCo, LCOV)'],
      ['malformed', 'the file could not be parsed'],
      ['truncated', 'the file ends mid-document — the job may have been killed while writing it'],
      ['tooLarge', 'the report exceeded the size limit'],
      ['noFiles', 'it parsed, but described no files'],
      ['missing', 'the uploaded attachment was not found'],
    ])('explains %s', (reason, expected) => {
      expect(component.rejectionText(reason)).toBe(expected);
    });

    // A reason added server-side after this client shipped must still say something.
    it('falls back to the raw reason when it is unknown', () => {
      expect(component.rejectionText('quotaExceeded')).toBe('quotaExceeded');
    });

    it('falls back to a generic sentence when there is no reason', () => {
      expect(component.rejectionText(null)).toBe('it could not be used');
      expect(component.rejectionText(undefined)).toBe('it could not be used');
    });
  });

  describe('derived state', () => {
    beforeEach(() => create());

    it('formats a flag rate, and a dash for 0/0', () => {
      component.flagTotals.set({
        unit: { linesCovered: 1, linesCoverable: 3, branchesCovered: 0, branchesTotal: 0, filesCount: 1 },
        e2e: { linesCovered: 0, linesCoverable: 0, branchesCovered: 0, branchesTotal: 0, filesCount: 0 },
      });
      expect(component.flagEntries()).toEqual([
        { flag: 'unit', rate: '33.3%' },
        { flag: 'e2e', rate: '—' },
      ]);
    });

    it('has no flag entries without totals', () => {
      component.flagTotals.set(null);
      expect(component.flagEntries()).toEqual([]);
    });

    it('splits the current path into cumulative breadcrumb segments', () => {
      expect(component.pathSegments()).toEqual([]);
      component.currentPath.set('src/app/core');
      expect(component.pathSegments()).toEqual([
        { name: 'src', path: 'src' },
        { name: 'app', path: 'src/app' },
        { name: 'core', path: 'src/app/core' },
      ]);
    });
  });

  describe('loading', () => {
    it('fetches the root tree, the hierarchy and the commit for its inputs', async () => {
      await create();

      expect(browse.getTree).toHaveBeenCalledWith('github', 'acme', 'widgets', 'abc1234567', undefined, undefined);
      expect(browse.getHierarchy).toHaveBeenCalledWith('github', 'acme', 'widgets', 'abc1234567');
      expect(browse.getCommit).toHaveBeenCalledWith('github', 'acme', 'widgets', 'abc1234567');
      expect(text()).toContain('a.ts');
      expect(text()).toContain('1/2');
      expect(text()).toContain('measured');
      expect(chart().componentInstance.data()).toEqual(hierarchy);
      expect(chart().componentInstance.rootId()).toBe('/');
    });

    it('reloads from the root when the sha input changes', async () => {
      await create();
      await component.openFolder('src');
      expect(component.currentPath()).toBe('src');

      fixture.componentRef.setInput('sha', 'def');
      await settle(fixture);

      expect(browse.getTree).toHaveBeenLastCalledWith('github', 'acme', 'widgets', 'def', undefined, undefined);
      expect(browse.getHierarchy).toHaveBeenLastCalledWith('github', 'acme', 'widgets', 'def');
      expect(component.currentPath()).toBe('');
      expect(component.chartRootId()).toBe('/');
    });

    it('shows a spinner while the tree is in flight', async () => {
      const pending = deferred<TreeResponse>();
      await create({ browse: { getTree: () => pending.promise } });

      expect(fixture.nativeElement.querySelector('bs-spinner')).not.toBeNull();
      pending.resolve(tree());
      await settle(fixture);
      expect(fixture.nativeElement.querySelector('bs-spinner')).toBeNull();
      expect(text()).toContain('No coverage data in this folder.');
    });

    // The token guard: a slow response for a folder the user already left must not
    // overwrite the folder they are looking at now.
    it('discards a tree response that a newer request superseded', async () => {
      await create();
      const older = deferred<TreeResponse>();
      const newer = deferred<TreeResponse>();
      browse.getTree.mockReturnValueOnce(older.promise).mockReturnValueOnce(newer.promise);

      const first = component.openFolder('old');
      const second = component.openFolder('new');
      newer.resolve(tree([{ name: 'new.ts', path: 'new/new.ts', isFile: true, linesCovered: 1, linesCoverable: 1 }]));
      await second;
      older.resolve(tree([{ name: 'old.ts', path: 'old/old.ts', isFile: true, linesCovered: 1, linesCoverable: 1 }]));
      await first;

      expect(component.currentPath()).toBe('new');
      expect(component.tree()!.entries.map((e) => e.name)).toEqual(['new.ts']);
    });

    it('also discards a superseded failure', async () => {
      await create();
      const older = deferred<TreeResponse>();
      browse.getTree.mockReturnValueOnce(older.promise).mockResolvedValueOnce(tree([
        { name: 'kept.ts', path: 'kept.ts', isFile: true, linesCovered: 1, linesCoverable: 1 },
      ]));

      const first = component.openFolder('old');
      await component.openFolder('');
      older.reject(new Error('late failure'));
      await first;

      expect(component.tree()!.entries.map((e) => e.name)).toEqual(['kept.ts']);
    });

    it('discards commit metadata of a sha that was replaced mid-flight', async () => {
      const staleHierarchy = deferred<CoverageHierarchyNode>();
      await create({ browse: { getHierarchy: () => staleHierarchy.promise } });

      const fresh: CoverageHierarchyNode = { id: '/', name: 'fresh' };
      browse.getHierarchy.mockResolvedValue(fresh);
      fixture.componentRef.setInput('sha', 'def');
      await settle(fixture);
      staleHierarchy.resolve({ id: '/', name: 'stale' });
      await settle(fixture);

      expect(component.hierarchy()).toEqual(fresh);
    });

    it('renders an empty folder when the tree fails', async () => {
      await create({ browse: { getTree: () => Promise.reject(new Error('500')) } });

      expect(component.tree()).toEqual({ buildId: '', entries: [], unmatchedFiles: [], unmatchedTotal: 0 });
      expect(text()).toContain('No coverage data in this folder.');
    });

    it('drops the chart but keeps the flags when the hierarchy fails', async () => {
      await create({
        browse: {
          getHierarchy: () => Promise.reject(new Error('500')),
          getCommit: () => Promise.resolve({
            id: 'c1', sha: 'abc', builds: [],
            flagTotals: { unit: { linesCovered: 1, linesCoverable: 2, branchesCovered: 0, branchesTotal: 0, filesCount: 1 } },
          } as CommitDetail),
        },
      });

      expect(component.hierarchy()).toBeNull();
      expect(chart()).toBeNull();
      expect(text()).toContain('a.ts');
      expect(text()).toContain('unit');
      expect(text()).toContain('50.0%');
    });

    it('drops flags and assembly when the commit fails', async () => {
      await create({ browse: { getCommit: () => Promise.reject(new Error('500')) } });

      expect(component.flagTotals()).toBeNull();
      expect(component.assembly()).toBeNull();
      expect(chart()).not.toBeNull();
    });
  });

  describe('rendering', () => {
    it('explains rejected reports instead of an empty folder', async () => {
      await create({
        browse: {
          getTree: () => Promise.resolve(tree([], {
            rejectedReports: [
              { fileName: 'lcov.info', reason: 'truncated', detail: 'line 40' },
              { fileName: 'x.xml', reason: null },
            ],
          })),
        },
      });

      const alert = fixture.nativeElement.querySelector('[data-testid="rejected-reports"]') as HTMLElement;
      expect(alert.textContent).toContain('lcov.info');
      expect(alert.textContent).toContain('the file ends mid-document');
      expect(alert.textContent).toContain('line 40');
      expect(alert.textContent).toContain('it could not be used');
      expect(text()).not.toContain('No coverage data in this folder.');
    });

    it('warns about unmatched paths and says when the sample is capped', async () => {
      await create({
        browse: { getTree: () => Promise.resolve(tree([], { unmatchedFiles: ['/ci/src/x.cs'], unmatchedTotal: 12 })) },
      });

      expect(text()).toContain("12 report path(s) couldn't be matched");
      expect(text()).toContain('/ci/src/x.cs');
      expect(text()).toContain('Showing 1 of 12 unmatched paths.');
    });

    it('marks a carried file with its origin commit', async () => {
      await create({
        browse: {
          getTree: () => Promise.resolve(tree([
            { name: 'c.ts', path: 'c.ts', isFile: true, linesCovered: 1, linesCoverable: 1, origin: 'Carried', carriedFromSha: '0123456789' },
          ])),
        },
      });

      expect(text()).toContain('carried');
      expect(text()).toContain('0123456');
      expect(text()).not.toContain('0123456789');
    });

    it('summarises the assembly and links the base commit', async () => {
      await create({
        browse: {
          getCommit: () => Promise.resolve({
            id: 'c1', sha: 'abc', builds: [],
            assembly: {
              completeness: 'Partial', incompleteReasons: ['affected'], measuredFiles: 3, carriedFiles: 7,
              unmeasuredFiles: 2, baseSha: 'bbbbbbbbbb', oldestOriginSha: 'oooooooooo', assembledAtUtc: '', builds: ['1', '2'],
            },
          } as CommitDetail),
        },
      });

      const summary = fixture.nativeElement.querySelector('[data-testid="assembly-summary"]') as HTMLElement;
      expect(summary.textContent).toContain('Partial');
      expect(summary.textContent).toContain('3 file(s) measured');
      expect(summary.textContent).toContain('7 carried unchanged from');
      expect(summary.textContent).toContain('oldest measured at');
      expect(summary.textContent).toContain('2 changed file(s) measured by nobody');
      expect(summary.textContent).toContain('2 builds');
      expect(summary.textContent).toContain('(affected)');
      expect(summary.querySelector('.text-bg-warning')).not.toBeNull();

      (summary.querySelector('a') as HTMLElement).click();
      expect(navigate).toHaveBeenCalledWith(['/', 'github', 'r', 'acme', 'widgets', 'c', 'bbbbbbbbbb']);
    });

    it('drills into a folder from the list', async () => {
      await create();
      const folderLink = [...fixture.nativeElement.querySelectorAll('td a')]
        .find((a: HTMLElement) => a.textContent?.trim() === 'src') as HTMLElement;
      folderLink.click();
      await settle(fixture);

      expect(browse.getTree).toHaveBeenLastCalledWith('github', 'acme', 'widgets', 'abc1234567', 'src', undefined);
      expect(component.chartRootId()).toBe('src');
      expect(fixture.nativeElement.querySelector('bs-breadcrumb')!.textContent).toContain('src');
    });
  });

  describe('flags', () => {
    const withFlags = {
      getCommit: () => Promise.resolve({
        id: 'c1', sha: 'abc', builds: [],
        flagTotals: { unit: { linesCovered: 1, linesCoverable: 2, branchesCovered: 0, branchesTotal: 0, filesCount: 1 } },
      } as CommitDetail),
    };

    it('applies a ?flag= already in the URL on the first load', async () => {
      await create({ queryParams: { flag: 'unit' } });

      expect(component.selectedFlag()).toBe('unit');
      expect(browse.getTree).toHaveBeenCalledWith('github', 'acme', 'widgets', 'abc1234567', undefined, 'unit');
    });

    // The URL is the source of truth: a back/forward that changes ?flag= re-opens the
    // current folder narrowed to it, without resetting to the root.
    it('re-opens the current folder when ?flag= changes', async () => {
      await create();
      await component.openFolder('src');
      browse.getTree.mockClear();

      route.setQueryParams({ flag: 'unit' });
      await settle(fixture);

      expect(component.selectedFlag()).toBe('unit');
      expect(browse.getTree).toHaveBeenCalledTimes(1);
      expect(browse.getTree).toHaveBeenCalledWith('github', 'acme', 'widgets', 'abc1234567', 'src', 'unit');
      expect(component.currentPath()).toBe('src');
    });

    it('does not refetch when ?flag= is unchanged', async () => {
      await create({ queryParams: { flag: 'unit' } });
      browse.getTree.mockClear();

      route.setQueryParams({ flag: 'unit', other: 'x' });
      await settle(fixture);

      expect(browse.getTree).not.toHaveBeenCalled();
    });

    it('selecting a chip navigates rather than fetching', async () => {
      await create({ browse: withFlags });
      browse.getTree.mockClear();

      const chip = [...fixture.nativeElement.querySelectorAll('button')]
        .find((b: HTMLElement) => b.textContent?.includes('unit')) as HTMLElement;
      expect(chip.getAttribute('aria-label')).toBe('unit coverage 50.0%');
      chip.click();

      expect(navigate).toHaveBeenCalledWith([], expect.objectContaining({
        queryParams: { flag: 'unit' }, queryParamsHandling: 'merge', replaceUrl: true,
      }));
      expect(browse.getTree).not.toHaveBeenCalled();
    });

    it('selecting the already-selected flag is a no-op', async () => {
      await create({ browse: withFlags, queryParams: { flag: 'unit' } });
      component.selectFlag('unit');
      expect(navigate).not.toHaveBeenCalled();

      component.selectFlag(null);
      expect(navigate).toHaveBeenCalledWith([], expect.objectContaining({ queryParams: { flag: null } }));
    });
  });

  describe('navigation', () => {
    beforeEach(() => create());

    it('openCommit goes to the commit page of the same repository', () => {
      component.openCommit('base123');
      expect(navigate).toHaveBeenCalledWith(['/', 'github', 'r', 'acme', 'widgets', 'c', 'base123']);
    });

    it('openFile goes to the file viewer with the path as a query param', () => {
      component.openFile('src/a.ts');
      expect(navigate).toHaveBeenCalledWith(
        ['/', 'github', 'r', 'acme', 'widgets', 'c', 'abc1234567', 'f'], { queryParams: { path: 'src/a.ts' } });
    });

    it('a file link in the list opens the file', async () => {
      const fileLink = [...fixture.nativeElement.querySelectorAll('td a')]
        .find((a: HTMLElement) => a.textContent?.trim() === 'a.ts') as HTMLElement;
      fileLink.click();
      expect(navigate).toHaveBeenCalledWith(
        ['/', 'github', 'r', 'acme', 'widgets', 'c', 'abc1234567', 'f'], { queryParams: { path: 'a.ts' } });
    });

    it('a chart zoom mirrors into the folder list; the root maps to ""', async () => {
      chart().componentInstance.zoom.emit({ node: { id: 'src' } });
      await settle(fixture);
      expect(browse.getTree).toHaveBeenLastCalledWith('github', 'acme', 'widgets', 'abc1234567', 'src', undefined);

      chart().componentInstance.zoom.emit({ node: { id: '/' } });
      await settle(fixture);
      expect(component.currentPath()).toBe('');
      expect(browse.getTree).toHaveBeenLastCalledWith('github', 'acme', 'widgets', 'abc1234567', undefined, undefined);
    });

    it('a chart zoom onto the folder already shown does not refetch', async () => {
      browse.getTree.mockClear();
      component.onChartZoom({ node: { id: '/' } } as never);
      expect(browse.getTree).not.toHaveBeenCalled();
    });

    it('selecting a chart leaf opens that file', () => {
      chart().componentInstance.nodeSelect.emit({ node: { id: 'src/b.ts' } });
      expect(navigate).toHaveBeenCalledWith(
        ['/', 'github', 'r', 'acme', 'widgets', 'c', 'abc1234567', 'f'], { queryParams: { path: 'src/b.ts' } });
    });

    it('the chart zoom root binds back into the component', () => {
      chart().componentInstance.rootId.set('src');
      expect(component.chartRootId()).toBe('src');
    });
  });
});
