import { ChangeDetectionStrategy, Component, TemplateRef, computed, inject, input, isDevMode, signal } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import {
  SPARK_AUTH_ROUTE_PATHS,
  SPARK_EXTERNAL_PROVIDERS,
  SparkAuthCapabilities,
  SparkExternalProvider,
  SparkExternalProviderPresentation,
  SparkExternalLoginError,
  SparkExternalLoginMode,
  SPARK_AUTH_CONFIG,
  isSafeReturnUrl,
  passkeysSupported,
} from '@mintplayer/ng-spark/auth/models';
import { SparkAuthService } from '@mintplayer/ng-spark/auth/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/auth/pipes';

/**
 * One provider's button, as the template sees it: the provider itself, and the call that signs in
 * with it.
 *
 * **Passing the closure is the point.** A consumer never touches `provider.scheme`, so the failure
 * this component exists to prevent — a hard-coded scheme string that silently stops matching when
 * the server's registration changes — is unreachable by construction. It also means busy state,
 * `returnUrl` handling and popup-versus-redirect can move behind `signIn` later without a
 * consumer-facing change.
 */
export interface SparkProviderButtonContext {
  $implicit: SparkExternalProviderView;
  signIn: () => void;
  /**
   * Whether a sign-in popup is open: disable the button while it is. Read from the service's
   * `externalLoginPending`, never from the sign-in promise (D2).
   */
  pending: boolean;
}

/** A provider the server reported, merged with whatever presentation the app declared for it. */
export interface SparkExternalProviderView extends SparkExternalProvider {
  iconClass?: string;
}

/**
 * The sign-in landing page: a button per external provider, and a link to the password form when the
 * application mounted one.
 *
 * The providers come from `GET /spark/auth/capabilities` rather than from a list the application
 * hard-codes. That is the point of the component: every consumer that hand-rolled this before wrote
 * the scheme name as a string literal, which is a silent mismatch waiting to happen the moment the
 * server's provider registration changes. A `withExternalLogin(githubProvider())` declaration only
 * *decorates* what the server reports — it cannot conjure a provider the server does not have.
 */
@Component({
  selector: 'spark-sign-in',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    NgTemplateOutlet, RouterLink, BsAlertComponent, BsCardComponent, BsCardHeaderComponent,
    BsSpinnerComponent, TranslateKeyPipe,
  ],
  templateUrl: './spark-sign-in.component.html',
})
export class SparkSignInComponent {
  private readonly authService = inject(SparkAuthService);
  private readonly declaredProviders = inject(SPARK_EXTERNAL_PROVIDERS, { optional: true }) ?? [];
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

  private readonly reported = signal<SparkExternalProvider[]>([]);
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

  /**
   * The translation key of the last external sign-in failure, from the popup's result or from the
   * `?sparkExternalLogin` a redirect came back with.
   */
  readonly externalError = signal('');

  /** `link_confirmation_sent` travels as a failure but is news (check your mail), not an error. */
  readonly externalErrorIsNotice = computed(() => this.externalError() === 'auth.externalLoginError.link_confirmation_sent');

  /**
   * The provider whose popup `window.open` refused. The page then offers "Continue in this tab":
   * the same provider in redirect mode. Not automatic (F10): in Firefox's installed web app a null
   * handle may still have started the flow inside the app, and a redirect would run a second one.
   */
  readonly blockedProvider = signal<SparkExternalProviderView | null>(null);

  /**
   * While a sign-in popup is open. The buttons follow this, not the promise: under COOP the popup
   * reads closed long before its result arrives, and the attempt keeps listening (D2).
   */
  readonly externalPending = computed(() => this.authService.externalLoginPending());

  /**
   * The server's list, decorated and ordered by whatever the application declared. Declared-but-not-
   * reported schemes are dropped and reported-but-not-declared ones keep a default button, so
   * neither side can produce a provider the other does not have.
   */
  readonly providers = computed<SparkExternalProviderView[]>(() => {
    const declarations = new Map<string, SparkExternalProviderPresentation>(
      this.declaredProviders.map(p => [p.scheme.toLowerCase(), p]),
    );

    return this.reported()
      .map((provider, index) => {
        const declared = declarations.get(provider.scheme.toLowerCase());
        return {
          provider: {
            ...provider,
            displayName: declared?.displayName ?? provider.displayName,
            iconClass: declared?.iconClass,
          } satisfies SparkExternalProviderView,
          // Undeclared providers sort after declared ones, keeping the server's relative order among
          // themselves — so adding a provider server-side appends a button rather than reshuffling.
          order: declared?.order ?? Number.MAX_SAFE_INTEGER,
          index,
        };
      })
      .sort((a, b) => a.order - b.order || a.index - b.index)
      .map(entry => entry.provider);
  });

  constructor() {
    // A redirect-mode round trip (inside an installed web app, or "Continue in this tab") comes back
    // with its failure in the URL.
    const returned = this.authService.takeExternalLoginResult();
    if (returned) this.showExternalError(returned);
    void this.load();
  }

  /** `popup_closed` is "not now", not an error, so it shows nothing. */
  private showExternalError(error: SparkExternalLoginError | undefined): void {
    if (error === 'popup_closed') return;
    this.externalError.set(`auth.externalLoginError.${error ?? 'no_login_info'}`);
  }

  private async load(): Promise<void> {
    try {
      const capabilities = await this.authService.capabilities();
      this.reported.set(capabilities.externalProviders);
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

  /** The closure handed to a projected template, so a consumer never names a scheme itself. */
  contextFor(provider: SparkExternalProviderView): SparkProviderButtonContext {
    return { $implicit: provider, signIn: () => this.signInWith(provider), pending: this.externalPending() };
  }

  /**
   * Signs in, then *leaves this page*.
   *
   * The navigation is the part that is easy to miss. In `'popup'` mode the `returnUrl` is consumed
   * by the popup — it tells the server where to send that window before it closes — and the opener,
   * which is the tab the user is actually looking at, is never touched. So without this the sign-in
   * succeeds, the topbar flips to the signed-in state, and the user is left staring at the login
   * page wondering whether it worked.
   *
   * Failures deliberately do not navigate: the page renders the error, and moving away would hide
   * it. `popup_closed` is not an error — it is "not now" — so it shows nothing. `popup_blocked` also
   * offers "Continue in this tab" ({@link continueInThisTab}).
   *
   * `mode` is left out unless given, so the configured `externalLoginMode` decides.
   */
  async signInWith(provider: SparkExternalProviderView, mode?: SparkExternalLoginMode): Promise<void> {
    this.externalError.set('');
    this.blockedProvider.set(null);
    const returnUrl = this.effectiveReturnUrl() ?? this.config.defaultRedirectUrl;
    const result = await this.authService.loginWithProvider(provider.scheme, mode ? { returnUrl, mode } : { returnUrl });
    if (result.success) {
      await this.router.navigateByUrl(returnUrl);
      return;
    }
    this.showExternalError(result.error);
    if (result.error === 'popup_blocked') this.blockedProvider.set(provider);
  }

  /** Retries the provider whose popup was blocked as a full-page redirect in this tab. */
  continueInThisTab(): Promise<void> {
    const provider = this.blockedProvider();
    return provider ? this.signInWith(provider, 'redirect') : Promise.resolve();
  }

  /**
   * Signs in with a passkey, then navigates — same shape as {@link signInWith}, and the navigation
   * matters for the same reason.
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
      // leaves popup_closed alone above.
      if (result.error === 'cancelled') return;

      this.passkeyError.set(result.error === 'locked_out' ? 'auth.lockedOut' : 'auth.passkeyFailed');
    } finally {
      this.passkeyBusy.set(false);
    }
  }
}

export default SparkSignInComponent;
