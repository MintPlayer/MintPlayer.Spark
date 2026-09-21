import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterModule } from '@angular/router';
import type { QueryResultItem, SparkRow } from '@mintplayer/ng-spark/models';
import { valueFor } from '@mintplayer/ng-spark/models';
import type { SparkAttributeColumnRenderer } from '@mintplayer/ng-spark/renderers';

/**
 * Spark column renderer "account-link": the account login, linking to its page.
 *
 * A renderer rather than the card's `rowRoute` input, because the accounts grid on the composed
 * Home page is auto-rendered by `spark-po-detail` — which forwards template slots but not
 * `rowRoute`, so from there the escape hatch is unreachable. MyAccountRow also has no `Read`
 * right (Query without Read is what publishes a list whose rows have no detail page), so the
 * framework link is null by design and this is the only navigation off the row.
 *
 * ⚠️ **The forge comes from the row, not from the cell.** Since M7 the account page is
 * `/{provider}/a/{login}`, and a login on its own no longer addresses anything — `mintplayer` on
 * GitHub and `mintplayer` on GitLab are different accounts. A column renderer receives only its
 * own value *by default*, but it also receives the whole row when it declares the `item` input,
 * which is where `Provider` is read from. Without that this renderer emitted `/a/{login}`, a
 * route that no longer exists.
 *
 * With no provider on the row the login renders as **plain text**: a link into a guessed forge
 * would resolve silently to the wrong account rather than to an error, which is the failure this
 * whole milestone exists to make impossible.
 */
@Component({
  selector: 'app-account-link-renderer',
  imports: [RouterModule],
  template: `
    @if (login(); as name) {
      @if (link(); as target) {
        <a [routerLink]="target">{{ name }}</a>
      } @else {
        {{ name }}
      }
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class AccountLinkRendererComponent implements SparkAttributeColumnRenderer {
  value = input<any>();
  options = input<Record<string, any> | undefined>();
  item = input<QueryResultItem | Record<string, any> | undefined>();

  readonly login = computed(() => {
    const value = this.value();
    return typeof value === 'string' && value.length > 0 ? value : null;
  });

  private readonly provider = computed(() => {
    const row = this.item();
    if (!row) return null;
    const value = valueFor(row as SparkRow, 'Provider')?.value;
    return typeof value === 'string' && value.length > 0 ? value : null;
  });

  readonly link = computed(() => {
    const provider = this.provider();
    const name = this.login();
    return provider && name ? ['/', provider, 'a', name] : null;
  });
}
