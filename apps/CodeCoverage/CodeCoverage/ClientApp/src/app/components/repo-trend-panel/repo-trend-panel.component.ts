import { ChangeDetectionStrategy, Component, effect, inject, input, signal, computed } from '@angular/core';
import { BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent } from '@mintplayer/ng-bootstrap/card';
import { BsTrendChartComponent } from '@mintplayer/ng-bootstrap/charts/trend';
import type { TrendSeries } from '@mintplayer/web-components/charts/trend';
import { BrowseService, HistoryPoint } from '../../services/browse.service';

/**
 * "Coverage over time" card (interactive trend chart), shared by the vanity
 * repo page and the generic /po Repository detail page. Self-fetches history;
 * hidden while there are fewer than two points (master behavior).
 */
@Component({
  selector: 'app-repo-trend-panel',
  imports: [BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent, BsTrendChartComponent],
  template: `
    @if (trendSeries().length > 0) {
      <bs-card class="mt-3 d-block">
        <bs-card-header><i class="bi bi-graph-up"></i> Coverage over time</bs-card-header>
        <bs-card-body>
          <div style="max-width: 640px;">
            <bs-trend-chart
              [series]="trendSeries()"
              [yMin]="0"
              [yMax]="100"
              [goal]="80"
              goalLabel="80% goal"
              inputLabel="Coverage over time" />
          </div>
        </bs-card-body>
      </bs-card>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class RepoTrendPanelComponent {
  private readonly browse = inject(BrowseService);

  owner = input.required<string>();
  name = input.required<string>();
  /** '' = the repository's default branch, which the server resolves. */
  branch = input<string>('');

  private readonly history = signal<HistoryPoint[]>([]);

  /**
   * Dates are only usable as x when every point has one (pre-FirstSeenAtUtc
   * documents may not) — otherwise fall back to the commit index.
   */
  readonly trendSeries = computed<TrendSeries[]>(() => {
    const history = this.history();
    if (history.length < 2) return [];
    const allDated = history.every((h) => !!h.timestamp);
    return [{
      id: 'coverage',
      label: 'Line coverage %',
      points: history.map((h, i) => ({ x: allDated ? new Date(h.timestamp!) : i, y: h.percent })),
    }];
  });

  constructor() {
    effect(async () => {
      const owner = this.owner();
      const name = this.name();
      const branch = this.branch();
      try {
        this.history.set(await this.browse.getHistory(owner, name, branch || undefined));
      } catch {
        this.history.set([]);
      }
    });
  }
}
