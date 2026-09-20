import { ChangeDetectionStrategy, Component, effect, inject, signal } from '@angular/core';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { RouterModule } from '@angular/router';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkAuthService } from '@mintplayer/ng-spark-auth/core';
import { AccountsService } from '../services/accounts.service';

/**
 * The two pieces of the Home page that are not attributes or rows: the reconnect banner and
 * the "install the App" hint.
 *
 * Both stay Angular, mounted through the poDetail route's `extraContentTemplate`, because
 * neither is data. The banner is a *remedy* — it points at /sign-in, which re-challenges
 * whichever provider needs it — and the hint's URL is per-environment, resolved from
 * `/api/me/accounts` rather than from anything the model could carry.
 *
 * ⚠️ The banner used to run an interactive popup through a GitHub-specific service, which is why
 * it carried its own error and in-flight state. Re-challenging through the shared sign-in page
 * costs a full navigation instead of a popup, and in exchange it works for every provider the
 * server reports rather than only for GitHub.
 *
 * It reads `/api/me/accounts` for those two facts only. The account list itself comes from the
 * `my-accounts` Spark query beside it; both are served by the same `IMyAccountsService`, so
 * there is one aggregation behind the page even though there are two round trips to it.
 */
@Component({
  selector: 'app-home-extras',
  imports: [RouterModule, BsAlertComponent, TranslateKeyPipe],
  template: `
    @if (reauthRequired()) {
      <bs-alert [type]="warningColor" [announce]="true" class="d-block mt-3">
        <div class="d-flex align-items-center gap-2 flex-wrap">
          <span class="me-auto">
            <i class="bi bi-exclamation-triangle"></i>
            {{ 'app.reauthBanner' | t }}
          </span>
          <a class="btn btn-sm btn-warning text-nowrap" routerLink="/sign-in">
            {{ 'app.reconnect' | t }}
          </a>
        </div>
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
  readonly authService = inject(SparkAuthService);

  readonly gitHubAppUrl = signal('https://github.com/apps/coverageproduction');
  readonly reauthRequired = signal(false);
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

}
