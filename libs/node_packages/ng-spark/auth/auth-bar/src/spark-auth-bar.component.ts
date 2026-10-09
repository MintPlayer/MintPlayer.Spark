import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink, Router } from '@angular/router';
import { BsButtonGroupComponent } from '@mintplayer/ng-bootstrap/button-group';
import {
  SPARK_AUTH_CONFIG,
  SPARK_AUTH_ROUTE_PATHS,
  findSparkAuthRoutePaths,
  resolveSignInUrl,
} from '@mintplayer/ng-spark/auth/models';
import { SparkAuthService } from '@mintplayer/ng-spark/auth/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/auth/pipes';

@Component({
  selector: 'spark-auth-bar',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, BsButtonGroupComponent, TranslateKeyPipe],
  templateUrl: './spark-auth-bar.component.html',
  // Outline buttons drawn in the surrounding text colour, not `btn-outline-light`: the bar sits in
  // whatever topbar the app has, and that is no longer guaranteed to be dark (#462). currentColor
  // follows the container in either theme; the hover tint is the same colour at low alpha.
  styles: [`
    /* One line, always: the bar lives in a fixed-height topbar. */
    :host {
      display: inline-flex;
      align-items: center;
      flex-wrap: nowrap;
      min-width: 0;
    }

    .spark-auth-bar-btn {
      --bs-btn-color: currentColor;
      --bs-btn-border-color: currentColor;
      --bs-btn-hover-color: currentColor;
      --bs-btn-hover-bg: color-mix(in srgb, currentColor 15%, transparent);
      --bs-btn-hover-border-color: currentColor;
      --bs-btn-active-color: currentColor;
      --bs-btn-active-bg: color-mix(in srgb, currentColor 25%, transparent);
      --bs-btn-active-border-color: currentColor;
    }
  `],
})
export class SparkAuthBarComponent {
  readonly authService = inject(SparkAuthService);
  readonly config = inject(SPARK_AUTH_CONFIG);
  private readonly router = inject(Router);

  /** Same target the guard and interceptor use, so the bar cannot link somewhere they would not send you. */
  readonly signInUrl = resolveSignInUrl(this.config, this.router);

  /**
   * Where the account overview lives, or `undefined` when `withAccount()` did not mount it — then the
   * bar offers logout alone rather than a link that would fall through to some other route.
   *
   * `SPARK_AUTH_ROUTE_PATHS` is provided by `sparkAuthRoutes()` on its own route subtree, and this bar
   * usually lives in the application shell, outside it, so the token is injected optionally and the
   * router configuration is the fallback. Reading the configuration makes a custom `account` path
   * exact here too, instead of assuming the default.
   */
  readonly accountUrl = (inject(SPARK_AUTH_ROUTE_PATHS, { optional: true })
    ?? findSparkAuthRoutePaths(this.router.config))?.account;

  async onLogout(): Promise<void> {
    try {
      await this.authService.logout();
    } finally {
      // Always navigate away from the authenticated area, even if the server-side
      // logout call fails (network error, session already expired, etc.). The local
      // session state has been cleared by SparkAuthService regardless.
      this.router.navigateByUrl('/');
    }
  }
}

export default SparkAuthBarComponent;
