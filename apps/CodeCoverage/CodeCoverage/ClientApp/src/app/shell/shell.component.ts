import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { RouterModule } from '@angular/router';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { Color } from '@mintplayer/ng-bootstrap';
import {
  SparkLanguageSelectorComponent, SparkShellComponent, SparkShellTopbarEndDirective, SparkShellMainHeaderDirective,
} from '@mintplayer/ng-spark/shell';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkAuthBarComponent } from '@mintplayer/ng-spark/auth/auth-bar';

/**
 * The application frame. All responsive behaviour — breakpoints, the overlay drawer,
 * dismiss-on-navigate, the toggler↔drawer mirror — belongs to `<spark-shell>` and the
 * `mp-shell` web component underneath it; this component owns only what is specific to
 * Coverage: the sign-in link and the login-error alert. The sign-in page itself is
 * ng-spark/auth's, and renders one button per provider the server reports — so adding a
 * forge changes nothing here.
 *
 * The sidebar menu is server-driven (`GET /spark/program-units`, already rights-filtered
 * per caller), so there are no router links here. A new entry goes in `programUnits.json`.
 *
 * Nothing here executes custom actions. Resync is declared on the Home persistent object
 * (`Resync/Home` in `security.json`, `showedOn: "detail"`), so Spark draws and runs it in
 * that page's own action bar — beside the grid it refreshes, rather than in app chrome that
 * every other route also shows.
 */
@Component({
  selector: 'app-shell',
  imports: [
    RouterModule,
    SparkShellComponent, SparkShellTopbarEndDirective, SparkShellMainHeaderDirective, SparkLanguageSelectorComponent,
    BsAlertComponent,
    TranslateKeyPipe,
    SparkAuthBarComponent,
  ],
  templateUrl: './shell.component.html',
  styleUrl: './shell.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ShellComponent {
  loginError = signal<string | null>(null);
  readonly dangerColor = Color.danger;

  // The dismiss button only hides the alert (visible model); clear the error so
  // the @if removes it and a later failure starts from a fresh, visible alert.
  onLoginAlertVisible(visible: boolean): void {
    if (!visible) this.loginError.set(null);
  }
}
