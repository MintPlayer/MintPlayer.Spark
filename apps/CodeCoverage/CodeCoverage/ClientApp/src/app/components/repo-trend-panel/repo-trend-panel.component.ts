import { ChangeDetectionStrategy, Component, effect, inject, input, signal, computed } from '@angular/core';
import { BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent } from '@mintplayer/ng-bootstrap/card';
import { SparkTrendChartComponent, SparkTrendOptions, SparkTrendValuePoint } from '@mintplayer/ng-spark/score-renderers';
import { BrowseService, HistoryPoint } from '../../services/browse.service';

/**
 * "Coverage over time" card (interactive trend chart), shared by the vanity
 * repo page and the generic /po Repository detail page. Self-fetches history;
 * hidden while there are fewer than two points (master behavior).
 */
@Component({
  selector: 'app-repo-trend-panel',
  imports: [BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent, SparkTrendChartComponent],
  template: `
    @if (trendPoints().length > 0) {
      <bs-card class="mt-3 d-block">
        <bs-card-header><i class="bi bi-graph-up"></i> Coverage over time</bs-card-header>
        <bs-card-body>
          <div style="max-width: 640px;">
            <spark-trend-chart [value]="trendPoints()" [options]="trendOptions" />
          </div>
        </bs-card-body>
      </bs-card>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class RepoTrendPanelComponent {
  private readonly browse = inject(BrowseService);

  /** Canonical forge spelling, e.g. "github". Sourced from the PO's document id (`forgeOf`). */
  provider = input.required<string>();
  owner = input.required<string>();
  name = input.required<string>();
  /** '' = the repository's default branch, which the server resolves. */
  branch = input<string>('');

  private readonly history = signal<HistoryPoint[]>([]);

  /**
   * ng-spark's trend chart: 0-100 with the 80% goal line, one series. Spelled out
   * rather than left to the renderer's defaults.
   */
  protected readonly trendOptions: SparkTrendOptions = {
    display: 'chart',
    yMin: 0,
    yMax: 100,
    goal: 80,
    goalLabel: '80% goal',
    label: 'Coverage over time',
    seriesId: 'coverage',
    seriesLabel: 'Line coverage %',
    minPoints: 2,
  };

  /**
   * Dates are only usable as x when every point has one (pre-FirstSeenAtUtc
   * documents may not) — otherwise fall back to the commit index.
   */
  readonly trendPoints = computed<SparkTrendValuePoint[]>(() => {
    const history = this.history();
    if (history.length < 2) return [];
    const allDated = history.every((h) => !!h.timestamp);
    return history.map((h, i) => ({ x: allDated ? new Date(h.timestamp!) : i, y: h.percent }));
  });

  constructor() {
    effect(async () => {
      const owner = this.owner();
      const name = this.name();
      const branch = this.branch();
      try {
        this.history.set(await this.browse.getHistory(this.provider(), owner, name, branch || undefined));
      } catch {
        this.history.set([]);
      }
    });
  }
}
