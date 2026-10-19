import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { formatMoney } from '@mintplayer/ng-spark/models';
import { SparkAttributeColumnRenderer, SparkAttributeDetailRenderer } from './spark-attribute-renderer';

/**
 * The core `money` renderer's display half (commerce X4): a `Money` value formatted with
 * `Intl.NumberFormat` in the viewer's locale (K11), on the detail page and in grid cells alike.
 *
 * Model sync writes `"renderer": "money"` on every `Money` attribute that has no renderer, so an app
 * gets this without registering anything; registering its own `money` renderer replaces it.
 *
 * `"rendererOptions": { "locale": "nl-BE" }` pins the locale instead of following the viewer, for a
 * column that must read the same for everyone.
 */
@Component({
  selector: 'spark-money-renderer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `{{ text() }}`,
  styleUrl: './money-renderer.component.scss',
})
export class SparkMoneyRendererComponent implements SparkAttributeDetailRenderer, SparkAttributeColumnRenderer {
  readonly value = input<any>();
  readonly options = input<Record<string, any> | undefined>();

  protected readonly text = computed(() => {
    const locale = this.options()?.['locale'];
    return typeof locale === 'string' && locale !== ''
      ? formatMoney(this.value(), locale)
      : formatMoney(this.value());
  });
}
