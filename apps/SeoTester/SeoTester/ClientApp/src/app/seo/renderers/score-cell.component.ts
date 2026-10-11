import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { SparkGaugeComponent } from '@mintplayer/ng-spark/score-renderers';

/** The gauge's diameter in a grid row; the detail page draws it at the model's `size`. */
export const SEO_SCORE_CELL_SIZE = 32;

/**
 * `seo-score`: ng-spark's gauge (letter grades) at the model's size on a detail page, and small in a grid row, so a list
 * of runs keeps its row height. The options are the model's (`rendererOptions`), only the size differs.
 */
@Component({
  selector: 'app-seo-score-cell',
  imports: [SparkGaugeComponent],
  template: `<spark-gauge [value]="value()" [options]="cellOptions()" />`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SeoScoreCellComponent {
  readonly value = input<unknown>();
  readonly options = input<Record<string, unknown> | undefined>();
  protected readonly cellOptions = computed(() => ({ ...(this.options() ?? {}), size: SEO_SCORE_CELL_SIZE }));
}
