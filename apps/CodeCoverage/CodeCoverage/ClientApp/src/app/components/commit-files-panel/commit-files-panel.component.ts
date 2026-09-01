import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { CommonModule } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsBreadcrumbComponent, BsBreadcrumbItemComponent } from '@mintplayer/ng-bootstrap/breadcrumb';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsHierarchyChartComponent, type HierarchyNodeEventDetail } from '@mintplayer/ng-bootstrap/charts/hierarchy';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { BrowseService, CoverageHierarchyNode, CoverageSummary, TreeResponse } from '../../services/browse.service';
import { CoverageBarComponent } from '../coverage-bar/coverage-bar.component';

/**
 * The "Files" card of a commit — sunburst hierarchy chart + drill-down folder
 * list — hosted by the generic /po Commit detail page (the vanity commit URLs
 * redirect there). Self-fetches tree + hierarchy; file clicks open the code viewer.
 */
@Component({
  selector: 'app-commit-files-panel',
  imports: [CommonModule, BsCardComponent, BsCardHeaderComponent, BsSpinnerComponent, BsAlertComponent, BsBreadcrumbComponent, BsBreadcrumbItemComponent, BsHierarchyChartComponent, BsTableComponent, CoverageBarComponent],
  templateUrl: './commit-files-panel.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class CommitFilesPanelComponent {
  private readonly router = inject(Router);
  // Non-routed component: this resolves to the hosting po/:type/:id route,
  // which is where the ?flag= query param lives.
  private readonly route = inject(ActivatedRoute);
  private readonly browse = inject(BrowseService);

  owner = input.required<string>();
  name = input.required<string>();
  sha = input.required<string>();

  readonly tree = signal<TreeResponse | null>(null);
  readonly currentPath = signal('');
  readonly hierarchy = signal<CoverageHierarchyNode | null>(null);
  // The chart's zoom root; node ids are repo paths, '/' is the data root.
  readonly chartRootId = signal<string | undefined>('/');
  readonly warningColor = Color.warning;

  // Per-flag totals of the shown build; a selected flag narrows the folder
  // list to that flag's own merged tree (the chart stays whole-build).
  readonly flagTotals = signal<Record<string, CoverageSummary> | null>(null);
  readonly selectedFlag = signal<string | null>(null);

  readonly flagEntries = computed(() => {
    const totals = this.flagTotals();
    if (!totals) return [];
    return Object.entries(totals).map(([flag, coverage]) => ({
      flag,
      rate: coverage.linesCoverable > 0
        ? `${((coverage.linesCovered / coverage.linesCoverable) * 100).toFixed(1)}%`
        : '—',
    }));
  });

  /** Segments of the current folder path, each with its cumulative path for the breadcrumb. */
  readonly pathSegments = computed(() => {
    const path = this.currentPath();
    if (!path) return [];
    const segments: { name: string; path: string }[] = [];
    let acc = '';
    for (const part of path.split('/')) {
      acc = acc ? `${acc}/${part}` : part;
      segments.push({ name: part, path: acc });
    }
    return segments;
  });

  // Monotonic request tokens: any signal write from an awaited response (or its
  // catch) is dropped when a newer request superseded it. Trees have their own
  // counter — a breadcrumb/flag click supersedes the tree fetch without
  // invalidating the in-flight hierarchy/commit metadata.
  private treeToken = 0;
  private metaToken = 0;
  // False until the first reload has run with usable inputs; the query-param
  // subscription must not fetch before then (input.required would throw).
  private initialized = false;

  constructor() {
    // The URL is the source of truth for the selected flag: chip clicks only
    // navigate (selectFlag), and this subscription applies the param — so
    // reload, back/forward and pasted links all take the same path. Kept
    // outside the reset effect on purpose: reading the param there would
    // re-create the U4 snap-back with the router as the tracked source.
    this.route.queryParamMap.pipe(takeUntilDestroyed()).subscribe((query) => {
      const flag = query.get('flag');
      if (flag === this.selectedFlag()) return;
      this.selectedFlag.set(flag);
      if (this.initialized) void this.openFolder(this.currentPath());
    });
    effect(() => {
      // Re-runs when owner/name/sha change: reset and reload. The body runs
      // untracked — openFolder reads selectedFlag before its first await, and
      // tracking it here would make selecting a flag re-trigger this reset
      // (the U4 snap-back of issue #13).
      const owner = this.owner();
      const name = this.name();
      const sha = this.sha();
      if (!owner || !name || !sha) return;
      untracked(() => this.reload(owner, name, sha));
    });
  }

  private reload(owner: string, name: string, sha: string): void {
    this.tree.set(null);
    this.hierarchy.set(null);
    this.currentPath.set('');
    this.chartRootId.set('/');
    this.selectedFlag.set(this.route.snapshot.queryParamMap.get('flag'));
    this.flagTotals.set(null);
    this.initialized = true;
    void this.openFolder('');
    void this.loadCommitMeta(owner, name, sha);
  }

  private async loadCommitMeta(owner: string, name: string, sha: string): Promise<void> {
    const token = ++this.metaToken;
    try {
      const hierarchy = await this.browse.getHierarchy(owner, name, sha);
      if (token !== this.metaToken) return;
      this.hierarchy.set(hierarchy);
    } catch {
      if (token !== this.metaToken) return;
      this.hierarchy.set(null);
    }
    try {
      const flagTotals = (await this.browse.getCommit(owner, name, sha)).flagTotals ?? null;
      if (token !== this.metaToken) return;
      this.flagTotals.set(flagTotals);
    } catch {
      if (token !== this.metaToken) return;
      this.flagTotals.set(null);
    }
  }

  async openFolder(path: string): Promise<void> {
    const token = ++this.treeToken;
    this.currentPath.set(path);
    this.chartRootId.set(path || '/');
    this.tree.set(null);
    try {
      const tree = await this.browse.getTree(this.owner(), this.name(), this.sha(), path || undefined, this.selectedFlag() ?? undefined);
      if (token !== this.treeToken) return;
      this.tree.set(tree);
    } catch {
      if (token !== this.treeToken) return;
      this.tree.set({ buildId: '', entries: [], unmatchedFiles: [], unmatchedTotal: 0 });
    }
  }

  selectFlag(flag: string | null): void {
    if (flag === this.selectedFlag()) return;
    // replaceUrl: chip clicks refine the current view rather than creating
    // history entries; the queryParamMap subscription applies the change.
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { flag },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  openFile(path: string): void {
    this.router.navigate(['/r', this.owner(), this.name(), 'c', this.sha(), 'f'], { queryParams: { path } });
  }

  // Chart → drill-down sync. Zooming a folder re-roots the chart itself (via
  // [(rootId)]); mirror it into the folder list. Selecting a leaf opens the file.
  onChartZoom(detail: HierarchyNodeEventDetail): void {
    const path = detail.node.id === '/' ? '' : detail.node.id;
    if (path !== this.currentPath()) {
      void this.openFolder(path);
    }
  }

  onChartSelect(detail: HierarchyNodeEventDetail): void {
    this.openFile(detail.node.id);
  }
}
