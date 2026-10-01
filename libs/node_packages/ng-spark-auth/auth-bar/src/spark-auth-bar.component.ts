import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink, Router } from '@angular/router';
import {
  SPARK_AUTH_CONFIG,
  SPARK_AUTH_ROUTE_PATHS,
  SparkAuthCapabilities,
  passkeysSupported,
  resolveSignInUrl,
} from '@mintplayer/ng-spark-auth/models';
import { SparkAuthService } from '@mintplayer/ng-spark-auth/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark-auth/pipes';

@Component({
  selector: 'spark-auth-bar',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, TranslateKeyPipe],
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

    .spark-auth-bar-user {
      max-width: 16rem;
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
   * Where the passkey page lives.
   *
   * ⚠️ Injected **optionally, and in practice it is absent**. `SPARK_AUTH_ROUTE_PATHS` is provided
   * by `sparkAuthRoutes()` on its own route subtree, while this bar lives in the application shell —
   * outside it. The token has no factory, so a required inject would throw rather than degrade.
   *
   * The fallback is `withPasskeys()`'s own default path, which is what an application that did not
   * override it mounted. That is a real limitation: an application that gave the page a custom path
   * *and* renders this bar from the shell gets the wrong link. Making this exact would mean exposing
   * the mounted paths at root rather than per-route — worth doing, and deliberately not invented here.
   */
  readonly passkeysUrl = inject(SPARK_AUTH_ROUTE_PATHS, { optional: true })?.passkeys ?? '/passkeys';

  private readonly capabilities = signal<SparkAuthCapabilities | null>(null);

  /**
   * Whether to offer passkey management at all.
   *
   * Asks the **server**, not the client's route table: the two are configured independently, which is
   * the whole reason `/spark/auth/capabilities` exists. `passkeys` reports that the server mounted
   * the passkey surface — the same `SparkPasskeys.Enabled` switch that mounts management.
   *
   * `passkeysSupported()` is the second half, and the capability's own documentation says why: the
   * flag is necessary but not sufficient, because a browser without WebAuthn would be offered a flow
   * it cannot start. An absent flag reads as "no" rather than leaking `undefined` into the template.
   */
  readonly showPasskeys = computed(() => this.capabilities()?.passkeys === true && passkeysSupported());

  constructor() {
    // One request per bar, and a failure leaves the link hidden rather than guessed: offering a
    // credential page that the server will refuse is worse than not offering it.
    void this.authService.capabilities()
      .then(capabilities => this.capabilities.set(capabilities))
      .catch(() => { /* stays null; showPasskeys() is false */ });
  }

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
