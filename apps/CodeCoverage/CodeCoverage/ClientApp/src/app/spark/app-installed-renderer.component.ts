import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import type { SparkAttributeColumnRenderer } from '@mintplayer/ng-spark/renderers';

/**
 * Spark column renderer "app-installed": whether the GitHub App is installed on the account,
 * as the translated green/grey badge the hand-written list drew.
 *
 * Deliberately not left as a plain `boolean` column, which renders a checkbox. "App installed"
 * is a status the user acts on — the colour and the wording are the message — and a checkbox
 * reads as an editable control on a page where nothing is editable.
 */
@Component({
  selector: 'app-app-installed-renderer',
  imports: [BsBadgeComponent, TranslateKeyPipe],
  template: `
    @if (installed()) {
      <bs-badge class="text-bg-success text-nowrap">{{ 'app.appInstalled' | t }}</bs-badge>
    } @else {
      <bs-badge class="text-bg-secondary text-nowrap">{{ 'app.appNotInstalled' | t }}</bs-badge>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class AppInstalledRendererComponent implements SparkAttributeColumnRenderer {
  value = input<any>();
  options = input<Record<string, any> | undefined>();

  readonly installed = computed(() => this.value() === true);
}
