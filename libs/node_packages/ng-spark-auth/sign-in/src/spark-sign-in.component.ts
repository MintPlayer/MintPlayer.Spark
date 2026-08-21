import { ChangeDetectionStrategy, Component, inject, isDevMode, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { SPARK_AUTH_ROUTE_PATHS, SparkAuthCapabilities, SparkExternalProvider } from '@mintplayer/ng-spark-auth/models';
import { SparkAuthService } from '@mintplayer/ng-spark-auth/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark-auth/pipes';

/**
 * The sign-in landing page for an application whose local credentials are turned off — a button per
 * external provider, and a link to the password form when there still is one.
 *
 * The providers come from `GET /spark/auth/capabilities` rather than from a list the application
 * hard-codes. That is the point of the component: every consumer that hand-rolled this before wrote
 * the scheme name as a string literal, which is a silent mismatch waiting to happen the moment the
 * server's provider registration changes.
 */
@Component({
  selector: 'spark-sign-in',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, BsAlertComponent, BsCardComponent, BsCardHeaderComponent, BsSpinnerComponent, TranslateKeyPipe],
  templateUrl: './spark-sign-in.component.html',
})
export class SparkSignInComponent {
  private readonly authService = inject(SparkAuthService);
  readonly routePaths = inject(SPARK_AUTH_ROUTE_PATHS);
  readonly colors = Color;

  readonly providers = signal<SparkExternalProvider[]>([]);
  readonly localCredentialsAvailable = signal(false);
  readonly loading = signal(true);
  readonly failed = signal(false);

  constructor() {
    void this.load();
  }

  private async load(): Promise<void> {
    try {
      const capabilities = await this.authService.capabilities();
      this.providers.set(capabilities.externalProviders);
      this.localCredentialsAvailable.set(capabilities.localCredentials !== 'Disabled');
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
        + 'localCredentials = Full. The two are configured independently — check that '
        + "sparkAuthRoutes({ localCredentials: … }) matches AddAuthentication's LocalCredentials mode.",
      );
    }
  }

  signInWith(provider: SparkExternalProvider): void {
    void this.authService.loginWithProvider(provider.scheme);
  }
}

export default SparkSignInComponent;
