import { ChangeDetectionStrategy, Component, computed, inject, input, LOCALE_ID } from '@angular/core';
import { formatDate } from '@angular/common';
import type { SparkAttributeColumnRenderer, SparkAttributeDetailRenderer } from '@mintplayer/ng-spark/renderers';

/**
 * Spark attribute renderer "date-time": renders a datetime attribute in the
 * viewer's locale (e.g. "Aug 15, 2026, 1:02:10 PM") instead of the raw ISO
 * string ng-spark falls back to today. `rendererOptions.format` overrides the
 * Angular date format ('medium' by default).
 *
 * Registered per attribute in the model JSON. Filed upstream as a framework
 * gap — datetime attributes should format like this out of the box, see
 * docs/adopt-spark-generic-ui.md M10.
 */
@Component({
  selector: 'app-date-time-renderer',
  template: `{{ formatted() }}`,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class DateTimeRendererComponent implements SparkAttributeColumnRenderer, SparkAttributeDetailRenderer {
  private readonly locale = inject(LOCALE_ID);

  value = input<any>();
  options = input<Record<string, any> | undefined>();

  readonly formatted = computed(() => {
    const value = this.value();
    if (value === null || value === undefined || value === '') return '';
    const format = this.options()?.['format'];
    try {
      return formatDate(value, typeof format === 'string' ? format : 'medium', this.locale);
    } catch {
      return String(value);
    }
  });
}
