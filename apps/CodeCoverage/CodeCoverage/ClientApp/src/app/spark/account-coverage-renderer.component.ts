import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { SparkAttributeColumnRenderer } from '@mintplayer/ng-spark/renderers';

/**
 * Spark column renderer "account-coverage": an account's aggregate coverage as a percentage.
 *
 * A number column would render "85.3", and the unit is not recoverable from the value — the
 * hand-written list always showed the "%". An account with no coverable lines has no percentage
 * rather than 0%: nothing measured is not the same as nothing covered.
 */
@Component({
  selector: 'app-account-coverage-renderer',
  template: `
    @if (percent(); as text) {
      <span>{{ text }}</span>
    } @else {
      <span class="text-muted">—</span>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class AccountCoverageRendererComponent implements SparkAttributeColumnRenderer {
  value = input<any>();
  options = input<Record<string, any> | undefined>();

  readonly percent = computed(() => {
    const value = this.value();
    if (value === null || value === undefined || value === '') return null;
    const n = Number(value);
    return Number.isNaN(n) ? null : `${n}%`;
  });
}
