import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { valueFor } from '@mintplayer/ng-spark/models';
import type { SparkAttributeColumnRenderer } from '@mintplayer/ng-spark/renderers';

/** The cell value the server sends for an organisation without an avatar (`MyAccountRowActions.GroupAvatar`). */
export const GROUP_AVATAR = 'icon:group';

/**
 * Spark column renderer "account-avatar": the account's GitHub avatar, falling back to a
 * person/organization icon.
 *
 * Not the built-in `image` data type, which emits an `<img>` only when the value is non-empty
 * and so renders an *empty cell* for an account we have no avatar for — the hand-written list
 * showed `bi-person`/`bi-people` there. Which of the two is decided on the server
 * (`MyAccountRowActions.WithAvatarFallback`): an organisation without an avatar arrives as the
 * {@link GROUP_AVATAR} marker in this very cell, so the row needs no separate `Type` value — which,
 * since #264, could only reach the row as a column of its own.
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
    return typeof value === 'string' && value.length > 0 && value !== GROUP_AVATAR ? value : null;
  });

  readonly alt = computed(() => {
    const login = valueFor(this.item(), 'Login')?.value;
    return typeof login === 'string' ? login : '';
  });

  readonly isUser = computed(() => this.value() !== GROUP_AVATAR);
}

