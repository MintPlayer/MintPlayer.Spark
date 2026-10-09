import { ChangeDetectionStrategy, Component, TemplateRef, inject, input, isDevMode, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import {
  SPARK_AUTH_ROUTE_PATHS,
  SparkAuthCapabilities,
  SPARK_AUTH_CONFIG,
  isSafeReturnUrl,
  passkeysSupported,
} from '@mintplayer/ng-spark/auth/models';
import { SparkAuthService } from '@mintplayer/ng-spark/auth/core';
import {
  SparkExternalLoginButtonsComponent,
  SparkProviderButtonContext,
} from '@mintplayer/ng-spark/auth/external-login';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/auth/pipes';

// Moved to @mintplayer/ng-spark/auth/external-login with the buttons themselves; re-exported so an
// import from this entry point keeps working.
export type { SparkProviderButtonContext, SparkExternalProviderView } from '@mintplayer/ng-spark/auth/external-login';

/**
 * The sign-in landing page: a button per external provider, and a link to the password form when the
 * application mounted one.
 *
 * The provider buttons are `<spark-external-login-buttons>`, which the login page hosts as well; this
 * page adds the passkey button, the link to the password form, and says so when there is no way to
 * sign in at all.
 */
@Component({
  selector: 'spark-sign-in',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink, BsAlertComponent, BsCardComponent, BsCardHeaderComponent,
    BsSpinnerComponent, SparkExternalLoginButtonsComponent, TranslateKeyPipe,
  ],
  templateUrl: './spark-sign-in.component.html',
})
export class SparkSignInComponent {
  private readonly authService = inject(SparkAuthService);
  private readonly route = inject(ActivatedRoute, { optional: true });
  private readonly router = inject(Router);
  private readonly config = inject(SPARK_AUTH_CONFIG);
  readonly routePaths = inject(SPARK_AUTH_ROUTE_PATHS);
  readonly colors = Color;

  /**
   * Where to land after a successful external sign-in.
   *
   * Falls back to a `?returnUrl=` query parameter, matching what the login page already does — so a
   * plain `<a routerLink="/sign-in" [queryParams]="{ returnUrl: '/somewhere' }">` works with no
   * wiring, which is what a routed page needs since the router passes it no inputs. The query value
   * is validated as a local path before use; an off-site one is dropped rather than followed.
   */
  readonly returnUrl = input<string | undefined>(undefined);

  private effectiveReturnUrl(): string | undefined {
    const explicit = this.returnUrl();
    if (explicit) return explicit;

    const fromQuery = this.route?.snapshot.queryParamMap.get('returnUrl');
    return isSafeReturnUrl(fromQuery) ? fromQuery! : undefined;
  }

  /**
   * Replaces the default provider button. Rendered once per provider with a
   * {@link SparkProviderButtonContext}.
   *
   * Reachable only when the consumer *hosts* this component — the router instantiates it with no
   * projected content, so on a default `withExternalLogin()` route there is nothing to project into.
   * Host it via `withExternalLogin({ signIn: { path: 'sign-in', component: MySignIn } })` and pass
   * the template from there.
   */
  readonly providerTemplate = input<TemplateRef<SparkProviderButtonContext> | null>(null);

  /** Whether the server reported any external provider; with none and no passkeys there is no way in. */
  readonly hasProviders = signal(false);
  readonly localCredentialsAvailable = signal(false);
  readonly loading = signal(true);
  readonly failed = signal(false);

  /**
   * Two conditions, and both are necessary. The server must have mounted the sign-in endpoint, and
   * this browser must be able to run the ceremony — offering the button on either half alone
   * produces a control that fails the moment it is clicked.
   */
  readonly passkeysAvailable = signal(false);
  readonly passkeyBusy = signal(false);
  readonly passkeyError = signal('');

  constructor() {
    void this.load();
  }

  private async load(): Promise<void> {
    try {
      // The provider buttons ask at the same time; the service makes that one request.
      const capabilities = await this.authService.capabilities();
      this.hasProviders.set((capabilities.externalProviders ?? []).length > 0);
      this.localCredentialsAvailable.set(capabilities.localCredentials !== 'Disabled');
      this.passkeysAvailable.set(capabilities.passkeys === true && passkeysSupported());
      this.warnOnMismatch(capabilities);
    } catch {
      // A sign-in page that renders nothing looks identical to one whose providers all failed to
      // load, so say which happened rather than showing an empty page.
      this.failed.set(true);
    } finally {
      this.loading.set(false);
    }
  }

  /**
   * The routes are a build-time decision and the server's mode a deployment-time one, so they can
   * disagree without anything failing loudly. Reaching this page at all means the app routed it,
   * which it only does when local credentials are meant to be limited.
   */
  private warnOnMismatch(capabilities: SparkAuthCapabilities): void {
    if (isDevMode() && capabilities.localCredentials === 'Full') {
      console.warn(
        '[ng-spark-auth] This app routes the sign-in landing page, but the server reports '
        + 'localCredentials = Full. The two are configured independently — check that the features '
        + "passed to sparkAuthRoutes() match AddAuthentication's LocalCredentials mode.",
      );
    }
  }

  /**
   * Signs in with a passkey, then navigates. The navigation matters for the same reason it does
   * after an external sign-in: nothing else moves the user off this page.
   *
   * No username is collected, and none should be: the server's request-options endpoint refuses to
   * accept an identity so that its answer cannot reveal whether an account exists. The browser
   * offers whatever discoverable credential it holds.
   */
  async signInWithPasskey(): Promise<void> {
    this.passkeyError.set('');
    this.passkeyBusy.set(true);
    try {
      const returnUrl = this.effectiveReturnUrl() ?? this.config.defaultRedirectUrl;
      const result = await this.authService.signInWithPasskey();

      if (result.success) {
        await this.router.navigateByUrl(returnUrl);
        return;
      }

      // Dismissing the authenticator prompt is "not now", not a failure — the same reasoning that
      // leaves popup_closed alone for the provider buttons.
      if (result.error === 'cancelled') return;

      this.passkeyError.set(result.error === 'locked_out' ? 'auth.lockedOut' : 'auth.passkeyFailed');
    } finally {
      this.passkeyBusy.set(false);
    }
  }
}

export default SparkSignInComponent;
