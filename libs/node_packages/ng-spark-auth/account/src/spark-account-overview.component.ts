import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { SparkAuthService } from '@mintplayer/ng-spark-auth/core';
import {
  SPARK_AUTH_ROUTE_PATHS,
  SparkAuthCapabilities,
  SparkAuthRoutePaths,
  passkeysSupported,
} from '@mintplayer/ng-spark-auth/models';
import { TranslateKeyPipe } from '@mintplayer/ng-spark-auth/pipes';

/**
 * Whether the server serves a page's feature. Mounting a page on the client is necessary but not
 * sufficient: the route table and the server's switches are configured independently, which is why
 * `/spark/auth/capabilities` exists. Pages without an entry need no switch beyond being mounted.
 */
type ServerCheck = (capabilities: SparkAuthCapabilities) => boolean;

/** Under `Disabled` neither `/login` nor `manage/password` is mapped: no password to change, and a
 * second factor would guard a sign-in that does not exist. */
const passwordSignIn: ServerCheck = c => c.localCredentials !== 'Disabled';

/** The account pages this app mounted, in display order, with their title keys. */
const PAGES: { key: keyof SparkAuthRoutePaths; label: string; server?: ServerCheck }[] = [
  { key: 'profile', label: 'auth.profileTitle' },
  { key: 'changePassword', label: 'auth.changePasswordTitle', server: passwordSignIn },
  { key: 'twoFactorSetup', label: 'auth.twoFactorSetupTitle', server: c => passwordSignIn(c) && c.twoFactor === true },
  { key: 'externalLogins', label: 'auth.externalLoginsTitle', server: c => c.externalLogins === true },
  // `passkeys` reports `SparkPasskeys.Enabled`; the browser must also support the ceremony.
  { key: 'passkeys', label: 'auth.passkeysTitle', server: c => c.passkeys === true && passkeysSupported() },
  { key: 'personalData', label: 'auth.personalDataTitle' },
];

/**
 * The account area's landing page (#460, D16): who is signed in, and a link per account page that is
 * both mounted (`withAccount()`) and served (`/spark/auth/capabilities`).
 */
@Component({
  selector: 'spark-account-overview',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, BsCardComponent, BsCardHeaderComponent, TranslateKeyPipe],
  template: `
    <div class="d-flex justify-content-center">
      <bs-card style="width: 100%; max-width: 640px;">
        <bs-card-header>
          <h3 class="mb-0">{{ 'auth.accountTitle' | t }}</h3>
          @if (auth.user(); as user) {
            <small class="spark-account-user text-muted text-break">{{ user.userName }}</small>
            @if (user.email && user.email !== user.userName) {
              <small class="spark-account-email text-muted text-break d-block">{{ user.email }}</small>
            }
          }
        </bs-card-header>
        <div class="p-4">
          <ul class="list-unstyled mb-0">
            @for (page of pages(); track page.key) {
              <li class="py-1"><a class="spark-account-link" [routerLink]="page.path">{{ page.label | t }}</a></li>
            }
          </ul>
        </div>
      </bs-card>
    </div>
  `,
})
export class SparkAccountOverviewComponent {
  protected readonly auth = inject(SparkAuthService);
  private readonly paths = inject(SPARK_AUTH_ROUTE_PATHS, { optional: true }) ?? {};
  private readonly capabilities = signal<SparkAuthCapabilities | null>(null);

  /**
   * Pages behind a server switch stay hidden until the capabilities arrive, and stay hidden if the
   * call fails: offering a page whose endpoints the server does not map is worse than omitting it.
   */
  protected readonly pages = computed(() => {
    const capabilities = this.capabilities();
    return PAGES
      .filter(page => !!this.paths[page.key])
      .filter(page => !page.server || (capabilities !== null && page.server(capabilities)))
      .map(page => ({ ...page, path: this.paths[page.key]! }));
  });

  constructor() {
    void this.auth.capabilities()
      .then(capabilities => this.capabilities.set(capabilities))
      .catch(() => { /* stays null; server-switched pages stay hidden */ });
  }
}
