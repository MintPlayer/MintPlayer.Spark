import { computed, Directive, input } from '@angular/core';
import type { SparkAttributeColumnRenderer, SparkAttributeDetailRenderer } from '@mintplayer/ng-spark/renderers';
import { SparkTrendOptions, toSparkTrendPoints } from './spark-trend-options';
import { numberOption, stringOption } from './option-readers';

// Internal: not exported from the entry point.

/** What the three trend components share: the inputs, the options read once, and the points. */
@Directive()
export abstract class SparkTrendBase implements SparkAttributeColumnRenderer, SparkAttributeDetailRenderer {
  readonly value = input<any>();
  readonly options = input<Record<string, any> | undefined>();

  protected readonly settings = computed(() => {
    const o = (this.options() ?? {}) as SparkTrendOptions;
    const label = typeof o.label === 'string' ? o.label : undefined;
    return {
      display: o.display === 'chart' ? 'chart' as const : 'sparkline' as const,
      yMin: typeof o.yMin === 'number' ? o.yMin : undefined,
      yMax: typeof o.yMax === 'number' ? o.yMax : undefined,
      label,
      minPoints: Math.max(1, Math.trunc(numberOption(o.minPoints, 2))),
      width: stringOption(o.width, '90px'),
      height: stringOption(o.height, '24px'),
      goal: typeof o.goal === 'number' ? o.goal : undefined,
      goalLabel: typeof o.goalLabel === 'string' ? o.goalLabel : undefined,
      seriesId: stringOption(o.seriesId, 'value'),
      seriesLabel: stringOption(o.seriesLabel, label ?? ''),
    };
  });

  protected readonly points = computed(() => toSparkTrendPoints(this.value(), this.settings().minPoints));
}
