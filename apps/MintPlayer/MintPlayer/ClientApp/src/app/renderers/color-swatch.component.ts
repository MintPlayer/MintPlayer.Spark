import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { SparkAttributeColumnRenderer, SparkAttributeDetailRenderer } from '@mintplayer/ng-spark/renderers';

/**
 * The `color-swatch` renderer of `TagCategory.Color`: a swatch beside the hex value. Only a `#rgb`/`#rrggbb` value is
 * painted, so an arbitrary string never reaches the style binding.
 */
@Component({
  selector: 'app-color-swatch',
  templateUrl: './color-swatch.component.html',
  styleUrl: './color-swatch.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ColorSwatchComponent implements SparkAttributeDetailRenderer, SparkAttributeColumnRenderer {
  readonly value = input<any>();

  protected readonly color = computed(() => {
    const value = this.value();
    return typeof value === 'string' && /^#([0-9a-f]{3}|[0-9a-f]{6})$/i.test(value) ? value : null;
  });
}
