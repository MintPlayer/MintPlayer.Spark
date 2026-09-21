import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import type { SparkAttributeColumnRenderer } from '@mintplayer/ng-spark/renderers';

/**
 * Spark column renderer "connected": whether the account has a working connection to its forge,
 * as the translated green/grey badge the hand-written list drew.
 *
 * ⚠ The question is deliberately "connected", not "is the GitHub App installed". Only GitHub
 * has an App to install; GitLab and Bitbucket answer the connection question differently. What
 * each forge means by connected is the forge library's business — this renderer only draws the
 * answer.
 *
 * Deliberately not left as a plain `boolean` column, which renders a checkbox. Connection state
 * is something the user acts on — the colour and the wording are the message — and a checkbox
 * reads as an editable control on a page where nothing is editable.
 */
@Component({
  selector: 'app-connected-renderer',
  imports: [BsBadgeComponent, TranslateKeyPipe],
  template: `
    @if (connected()) {
      <bs-badge class="text-bg-success text-nowrap">{{ 'app.connected' | t }}</bs-badge>
    } @else {
      <bs-badge class="text-bg-secondary text-nowrap">{{ 'app.notConnected' | t }}</bs-badge>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ConnectedRendererComponent implements SparkAttributeColumnRenderer {
  value = input<any>();
  options = input<Record<string, any> | undefined>();

  readonly connected = computed(() => this.value() === true);
}
