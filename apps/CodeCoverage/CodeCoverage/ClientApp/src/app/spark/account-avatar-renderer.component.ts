import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { valueFor } from '@mintplayer/ng-spark/models';
import type { SparkAttributeColumnRenderer } from '@mintplayer/ng-spark/renderers';

/**
 * Spark column renderer "account-avatar": the account's GitHub avatar, falling back to a
 * person/organization icon.
 *
 * Not the built-in `image` data type, which emits an `<img>` only when the value is non-empty
 * and so renders an *empty cell* for an account we have no avatar for — the hand-written list
 * showed `bi-person`/`bi-people` there. The fallback needs `Type`, which the model declares
 * `showedOn: "Query", isVisible: false` for exactly this reason.
 *
 * The corner radius used to be an inline style, because the cell drew inside `mp-datatable`'s
 * shadow root where a Bootstrap class did not reach. ng-bootstrap 22.18.0 moved the datatable to
 * the light DOM, so `rounded-1` (`--bs-border-radius-sm`, 0.25rem — the same value) applies.
 */
@Component({
  selector: 'app-account-avatar-renderer',
  template: `
    @if (src(); as url) {
      <img [src]="url" [alt]="alt()" width="24" height="24" class="rounded-1">
    } @else {
      <i class="bi" [class.bi-person]="isUser()" [class.bi-people]="!isUser()"></i>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class AccountAvatarRendererComponent implements SparkAttributeColumnRenderer {
  value = input<any>();
  options = input<Record<string, any> | undefined>();
  item = input<any>();

  readonly src = computed(() => {
    const value = this.value();
    return typeof value === 'string' && value.length > 0 ? value : null;
  });

  readonly alt = computed(() => {
    const login = valueFor(this.item(), 'Login')?.value;
    return typeof login === 'string' ? login : '';
  });

  readonly isUser = computed(() => {
    const type = valueFor(this.item(), 'Type')?.value;
    return typeof type !== 'string' || !GROUP_ACCOUNT_TYPES.has(type.toLowerCase());
  });
}

/**
 * The account types that mean "several people", across the forges we know about: GitHub says
 * `Organization`, GitLab says `group`, Bitbucket says `workspace` or `team`.
 *
 * ⚠ Matched as a deny-list on purpose, so an unrecognised value draws the person icon rather than
 * nothing. `Type` is whatever the forge library stored — this renderer never sees the forge — so
 * the one thing it must not do is assume the value it does not know is an organisation. Getting it
 * wrong picks the wrong icon; the previous `=== 'User'` test got it wrong for every forge but one.
 */
const GROUP_ACCOUNT_TYPES = new Set(['organization', 'organisation', 'group', 'team', 'workspace']);
