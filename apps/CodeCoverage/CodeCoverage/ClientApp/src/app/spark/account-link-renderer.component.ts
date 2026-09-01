import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterModule } from '@angular/router';
import type { SparkAttributeColumnRenderer } from '@mintplayer/ng-spark/renderers';

/**
 * Spark column renderer "account-link": the account login, linking to its page.
 *
 * A renderer rather than the card's `rowRoute` input, because the accounts grid on the composed
 * Home page is auto-rendered by `spark-po-detail` — which forwards template slots but not
 * `rowRoute`, so from there the escape hatch is unreachable. MyAccountRow also has no `Read`
 * right (Query without Read is what publishes a list whose rows have no detail page), so the
 * framework link is null by design and this is the only navigation off the row.
 */
@Component({
  selector: 'app-account-link-renderer',
  imports: [RouterModule],
  template: `
    @if (login(); as name) {
      <a [routerLink]="['/a', name]">{{ name }}</a>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class AccountLinkRendererComponent implements SparkAttributeColumnRenderer {
  value = input<any>();
  options = input<Record<string, any> | undefined>();

  readonly login = computed(() => {
    const value = this.value();
    return typeof value === 'string' && value.length > 0 ? value : null;
  });
}
