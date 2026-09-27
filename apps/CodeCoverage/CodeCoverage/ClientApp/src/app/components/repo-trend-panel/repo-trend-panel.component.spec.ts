import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { BsTrendChartComponent } from '@mintplayer/ng-bootstrap/charts/trend';
import { describe, expect, it } from 'vitest';
import { HistoryPoint } from '../../services/browse.service';
import { BrowseServiceStub, createBrowseStub, provideBrowseStub, settle, StubTrendChart } from '../../../testing/test-utils';
import { RepoTrendPanelComponent } from './repo-trend-panel.component';

function point(sha: string, percent: number, timestamp?: string): HistoryPoint {
  return { sha, percent, timestamp, linesCovered: percent, linesCoverable: 100 };
}

describe('RepoTrendPanelComponent', () => {
  let fixture: ComponentFixture<RepoTrendPanelComponent>;
  let browse: BrowseServiceStub;

  async function create(getHistory: (...args: any[]) => Promise<HistoryPoint[]>, branch?: string) {
    browse = createBrowseStub({ getHistory });
    TestBed.configureTestingModule({
      imports: [RepoTrendPanelComponent],
      providers: [provideZonelessChangeDetection(), provideBrowseStub(browse)],
    });
    TestBed.overrideComponent(RepoTrendPanelComponent, {
      remove: { imports: [BsTrendChartComponent] },
      add: { imports: [StubTrendChart] },
    });
    fixture = TestBed.createComponent(RepoTrendPanelComponent);
    fixture.componentRef.setInput('provider', 'github');
    fixture.componentRef.setInput('owner', 'acme');
    fixture.componentRef.setInput('name', 'widgets');
    if (branch !== undefined) fixture.componentRef.setInput('branch', branch);
    await settle(fixture);
  }

  const chart = () => fixture.debugElement.query(By.directive(StubTrendChart))?.componentInstance as StubTrendChart | undefined;

  it('plots dated history against time with the 80% goal', async () => {
    await create(() => Promise.resolve([
      point('a', 50, '2026-01-01T00:00:00Z'),
      point('b', 75, '2026-02-01T00:00:00Z'),
    ]));

    expect(browse.getHistory).toHaveBeenCalledWith('github', 'acme', 'widgets', undefined);
    expect(fixture.nativeElement.textContent).toContain('Coverage over time');
    const c = chart()!;
    expect(c.yMin()).toBe(0);
    expect(c.yMax()).toBe(100);
    expect(c.goal()).toBe(80);
    expect(c.goalLabel()).toBe('80% goal');
    expect(c.series()).toEqual([{
      id: 'coverage',
      label: 'Line coverage %',
      points: [
        { x: new Date('2026-01-01T00:00:00Z'), y: 50 },
        { x: new Date('2026-02-01T00:00:00Z'), y: 75 },
      ],
    }]);
  });

  // One undated point makes dates unusable as x; the commit index is used for all.
  it('falls back to the commit index when any point lacks a timestamp', async () => {
    await create(() => Promise.resolve([
      point('a', 50, '2026-01-01T00:00:00Z'),
      point('b', 60),
      point('c', 70, '2026-03-01T00:00:00Z'),
    ]));

    expect((chart()!.series()[0] as { points: unknown[] }).points).toEqual([
      { x: 0, y: 50 }, { x: 1, y: 60 }, { x: 2, y: 70 },
    ]);
  });

  it('stays hidden with fewer than two points', async () => {
    await create(() => Promise.resolve([point('a', 50, '2026-01-01T00:00:00Z')]));
    expect(chart()).toBeUndefined();
    expect(fixture.nativeElement.querySelector('bs-card')).toBeNull();
  });

  it('stays hidden when the history cannot be loaded', async () => {
    await create(() => Promise.reject(new Error('500')));
    expect(chart()).toBeUndefined();
    expect(fixture.nativeElement.textContent).not.toContain('Coverage over time');
  });

  it('asks for a named branch, and refetches when it changes', async () => {
    await create(() => Promise.resolve([]), 'dev');
    expect(browse.getHistory).toHaveBeenCalledWith('github', 'acme', 'widgets', 'dev');

    fixture.componentRef.setInput('branch', '');
    await settle(fixture);
    expect(browse.getHistory).toHaveBeenLastCalledWith('github', 'acme', 'widgets', undefined);
  });

  it('hides a chart that was showing once a refetch fails', async () => {
    await create(() => Promise.resolve([point('a', 1), point('b', 2)]));
    expect(chart()).toBeDefined();

    browse.getHistory.mockRejectedValue(new Error('500'));
    fixture.componentRef.setInput('branch', 'gone');
    await settle(fixture);
    expect(chart()).toBeUndefined();
  });
});
