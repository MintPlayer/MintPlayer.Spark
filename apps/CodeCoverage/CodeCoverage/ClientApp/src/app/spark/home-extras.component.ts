import { ChangeDetectionStrategy, Component, effect, inject, signal } from '@angular/core';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkAuthService } from '@mintplayer/ng-spark-auth/core';
import { AccountsService } from '../services/accounts.service';
import { GitHubLoginService } from '../services/github-login.service';
import { HOME_URL } from './home-route';

/**
 * The two pieces of the Home page that are not attributes or rows: the "reconnect GitHub"
 * banner and the "install the App" hint.
 *
 * Both stay Angular, mounted through the poDetail route's `extraContentTemplate`, because
 * neither is data. The banner is a *remedy* — it exists to run an interactive popup flow that
 * must be user-gesture-initiated — and the hint's URL is per-environment, resolved from
 * `/api/me/accounts` rather than from anything the model could carry.
 *
 * It reads `/api/me/accounts` for those two facts only. The account list itself comes from the
 * `my-accounts` Spark query beside it; both are served by the same `IMyAccountsService`, so
 * there is one aggregation behind the page even though there are two round trips to it.
 */
@Component({
  selector: 'app-home-extras',
  imports: [BsAlertComponent, TranslateKeyPipe],
  template: `
    @if (reauthRequired()) {
      <bs-alert [type]="warningColor" [announce]="true" class="d-block mt-3">
        <div class="d-flex align-items-center gap-2 flex-wrap">
          <span class="me-auto">
            <i class="bi bi-exclamation-triangle"></i>
            {{ 'app.reauthBanner' | t }}
          </span>
          <button class="btn btn-sm btn-warning text-nowrap" (click)="reconnect()" [disabled]="reconnecting()">
            <i class="bi bi-github"></i> {{ 'app.reconnectGitHub' | t }}
          </button>
        </div>
        @if (reconnectError(); as err) {
          <p class="small mb-0 mt-2">{{ err }}</p>
        }
      </bs-alert>
    }

    @if (authService.user()?.isAuthenticated) {
      <p class="text-muted small mt-3 mb-0">
        {{ 'app.installAppHintBefore' | t }}
        <a [href]="gitHubAppUrl()" target="_blank" rel="noopener">{{ 'app.installAppHintLink' | t }}</a>
        {{ 'app.installAppHintAfter' | t }}
      </p>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class HomeExtrasComponent {
  private readonly accountsService = inject(AccountsService);
  private readonly gitHubLogin = inject(GitHubLoginService);
  readonly authService = inject(SparkAuthService);

  readonly gitHubAppUrl = signal('https://github.com/apps/coverageproduction');
  readonly reauthRequired = signal(false);
  readonly reconnecting = signal(false);
  readonly reconnectError = signal<string | null>(null);
  readonly warningColor = Color.warning;

  constructor() {
    effect(() => {
      if (this.authService.user()?.isAuthenticated) {
        void this.load();
      } else {
        this.reauthRequired.set(false);
      }
    });
  }

  private async load(): Promise<void> {
    try {
      const response = await this.accountsService.getMyAccounts();
      this.gitHubAppUrl.set(response.gitHubAppUrl);
      this.reauthRequired.set(response.gitHubReauthRequired ?? false);
    } catch {
      // The banner is an escalation, not a diagnosis: if we cannot tell whether the
      // token is dead, saying nothing beats claiming it is.
      this.reauthRequired.set(false);
    }
  }

  // Button-gated on purpose: popups must be user-gesture-initiated, and calling
  // loginWithProvider from the auth effect would re-enter forever. A successful popup
  // re-authorizes AND re-saves fresh tokens (the Spark callback overwrites stored tokens
  // on every success), so the reload below rebuilds visibility with a working token.
  async reconnect(): Promise<void> {
    this.reconnectError.set(null);
    this.reconnecting.set(true);
    try {
      const result = await this.gitHubLogin.login(HOME_URL);
      if (result.success) {
        await this.accountsService.resync();
        await this.load();
        return;
      }
      if (result.error === 'popup_closed') return; // "not now" — no error banner
      this.reconnectError.set(result.message ?? null);
    } finally {
      this.reconnecting.set(false);
    }
  }
}
