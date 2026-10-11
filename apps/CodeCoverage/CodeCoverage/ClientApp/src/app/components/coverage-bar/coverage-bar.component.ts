import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { SparkThresholdBarComponent, SparkThresholdBarOptions, sparkScoreColor, sparkTrafficLightBands } from '@mintplayer/ng-spark/score-renderers';
import { CoverageSummary, coveragePercent } from '../../services/browse.service';

/**
 * How coverage draws as a bar: ng-spark's threshold bar on 0–100, green from 80 and amber from 60
 * (the badge's thresholds), one decimal and a %. Spelled out rather than left to the renderer's
 * defaults, so a change of those defaults cannot change this production page.
 */
export const COVERAGE_BAR_OPTIONS: SparkThresholdBarOptions = {
  min: 0,
  max: 100,
  bands: sparkTrafficLightBands,
  decimals: 1,
  suffix: '%',
  label: 'Coverage',
  width: '80px',
  height: '8px',
  showValue: true,
};

/** Compact coverage percentage with a colored progress bar, for table cells. */
@Component({
  selector: 'app-coverage-bar',
  imports: [SparkThresholdBarComponent],
  template: `<spark-threshold-bar [value]="percent()" [options]="options" />`,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class CoverageBarComponent {
  summary = input<CoverageSummary | null | undefined>();

  protected readonly options = COVERAGE_BAR_OPTIONS as Record<string, any>;

  readonly percent = computed(() => coveragePercent(this.summary()));

  readonly color = computed(() => sparkScoreColor(this.percent(), sparkTrafficLightBands));
}
