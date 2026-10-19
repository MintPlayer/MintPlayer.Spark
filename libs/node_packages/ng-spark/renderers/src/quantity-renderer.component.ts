import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { asPackagingLevels, formatQuantity, formatQuantityBreakdown } from '@mintplayer/ng-spark/models';
import { SparkAttributeColumnRenderer, SparkAttributeDetailRenderer } from './spark-attribute-renderer';

/**
 * The core `quantity` renderer's display half (warehouse WX8): a `Quantity` value as a localized number
 * and its unit (`1,234.5 EA`), on the detail page and in grid cells alike.
 *
 * Model sync writes `"renderer": "quantity"` on every `Quantity` attribute that has no renderer.
 *
 * `rendererOptions`:
 * - `locale`: pins the locale instead of following the viewer.
 * - `baseUnit` + `levels` (`[{ "unit": "CS", "baseQuantity": 12 }]`): shows the value as whole packages,
 *   `2 CS + 3 EA`, the server's `PackagingLevels.Breakdown` rule.
 */
@Component({
  selector: 'spark-quantity-renderer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `{{ text() }}`,
  styleUrl: './quantity-renderer.component.scss',
})
export class SparkQuantityRendererComponent implements SparkAttributeDetailRenderer, SparkAttributeColumnRenderer {
  readonly value = input<any>();
  readonly options = input<Record<string, any> | undefined>();

  protected readonly text = computed(() => {
    const options = this.options();
    const locale = typeof options?.['locale'] === 'string' && options['locale'] !== '' ? options['locale'] as string : undefined;
    const baseUnit = options?.['baseUnit'];
    const levels = asPackagingLevels(options?.['levels']);
    if (typeof baseUnit === 'string' && baseUnit !== '' && levels.length > 0)
      return locale ? formatQuantityBreakdown(this.value(), baseUnit, levels, locale) : formatQuantityBreakdown(this.value(), baseUnit, levels);
    return locale ? formatQuantity(this.value(), locale) : formatQuantity(this.value());
  });
}
