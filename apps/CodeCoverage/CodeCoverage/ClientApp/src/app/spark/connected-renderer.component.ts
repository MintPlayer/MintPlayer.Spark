import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import type { SparkAttributeColumnRenderer } from '@mintplayer/ng-spark/renderers';
import { ChipComponent } from '../components/chip/chip.component';

/**
 * Spark column renderer "connected": whether the account has a working connection to its forge,
 * as a translated green/grey chip, where the hand-written list drew a badge.
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
  imports: [ChipComponent, TranslateKeyPipe],
  template: `
    @if (connected()) {
      <app-chip color="success">{{ 'app.connected' | t }}</app-chip>
    } @else {
      <app-chip color="secondary">{{ 'app.notConnected' | t }}</app-chip>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ConnectedRendererComponent implements SparkAttributeColumnRenderer {
  value = input<any>();
  options = input<Record<string, any> | undefined>();

  readonly connected = computed(() => this.value() === true);
}
