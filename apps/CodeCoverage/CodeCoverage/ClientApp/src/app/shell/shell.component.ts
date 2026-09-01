import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { KeyValuePipe } from '@angular/common';
import { RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { BsAlertComponent, BsAlertCloseComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsSelectComponent, BsSelectOption } from '@mintplayer/ng-bootstrap/select';
import { Color } from '@mintplayer/ng-bootstrap';
import { SparkShellComponent, SparkShellTopbarEndDirective, SparkShellMainHeaderDirective } from '@mintplayer/ng-spark/shell';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { ResolveTranslationPipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkAuthService } from '@mintplayer/ng-spark-auth/core';
import { GitHubLoginService } from '../services/github-login.service';
import { HOME_URL } from '../spark/home-route';

/**
 * The application frame. All responsive behaviour — breakpoints, the overlay drawer,
 * dismiss-on-navigate, the toggler↔drawer mirror — belongs to `<spark-shell>` and the
 * `mp-shell` web component underneath it; this component owns only what is specific to
 * Coverage: the GitHub sign-in block and the login-error alert.
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
    RouterModule, FormsModule, KeyValuePipe,
    SparkShellComponent, SparkShellTopbarEndDirective, SparkShellMainHeaderDirective,
    BsAlertComponent, BsAlertCloseComponent, BsSelectComponent, BsSelectOption,
    ResolveTranslationPipe, TranslateKeyPipe,
  ],
  templateUrl: './shell.component.html',
  styleUrl: './shell.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ShellComponent {
  private readonly gitHubLogin = inject(GitHubLoginService);
  readonly authService = inject(SparkAuthService);
  readonly lang = inject(SparkLanguageService);

  loginError = signal<string | null>(null);
  readonly dangerColor = Color.danger;

  // The flow itself (popup handshake, blocked → redirect fallback, error map)
  // lives in GitHubLoginService, shared with home's reconnect banner. Every
  // failure is surfaced here: a popup that closes with no visible effect
  // reads as "broken".
  async loginWithGitHub(): Promise<void> {
    this.loginError.set(null);
    const result = await this.gitHubLogin.login(HOME_URL);
    if (!result.success) this.loginError.set(result.message ?? null);
  }

  async logout(): Promise<void> {
    await this.authService.logout();
  }

  // bs-alert-close only hides the alert (isVisible model); clear the error so
  // the @if removes it and a later failure starts from a fresh, visible alert.
  onLoginAlertVisible(visible: boolean): void {
    if (!visible) this.loginError.set(null);
  }
}
