import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { SparkAuthService } from '@mintplayer/ng-spark-auth/core';
import {
  SPARK_EXTERNAL_PROVIDERS,
  SparkExternalLogins,
  SparkExternalProviderPresentation,
} from '@mintplayer/ng-spark-auth/models';
import { TranslateKeyPipe } from '@mintplayer/ng-spark-auth/pipes';

/**
 * Connected logins (#460, D16): the providers attached to the account (remove, unless it is the last
 * way in — the server's `canUnlink`, and its refusal), and the configured providers that could be
 * attached (a popup handshake keyed on the signed-in user).
 *
 * Presentation (icon, display name) comes from the same `externalProvider()` declarations as the
 * sign-in page when they are in scope; the server stays authoritative over which providers exist.
 */
@Component({
  selector: 'spark-external-logins',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BsAlertComponent, BsCardComponent, BsCardHeaderComponent, BsSpinnerComponent, TranslateKeyPipe],
  template: `
    <div class="d-flex justify-content-center">
      <bs-card style="width: 100%; max-width: 640px;">
        <bs-card-header><h3 class="mb-0">{{ 'auth.externalLoginsTitle' | t }}</h3></bs-card-header>
        <div class="p-4">
          @if (errorKey()) {
            <bs-alert [type]="colors.danger" class="mb-3 d-block spark-account-error">{{ errorKey() | t }}</bs-alert>
          }
          @if (loading()) {
            <div class="text-center"><bs-spinner /></div>
          } @else if (logins(); as l) {
            @for (login of l.linked; track login.provider + login.providerKey) {
              <div class="d-flex align-items-center justify-content-between border-bottom py-2 spark-linked-login">
                <span>
                  @if (iconFor(login.provider); as icon) { <i [class]="icon" class="me-2"></i> }
                  {{ nameFor(login.provider, login.displayName) }}
                </span>
                <button type="button" class="btn btn-outline-danger btn-sm" [disabled]="busy() || !login.canUnlink"
                        [attr.title]="login.canUnlink ? null : ('auth.lastCredentialHint' | t)"
                        (click)="unlink(login.provider, login.providerKey)">
                  {{ 'auth.removeLogin' | t }}
                </button>
              </div>
            } @empty {
              <p class="text-muted">{{ 'auth.noLinkedLogins' | t }}</p>
            }

            @if (l.available.length) {
              <h5 class="mt-4">{{ 'auth.addLogin' | t }}</h5>
              <div class="d-flex flex-wrap gap-2">
                @for (provider of l.available; track provider.provider) {
                  <button type="button" class="btn btn-outline-primary spark-available-login" [disabled]="busy()" (click)="link(provider.provider)">
                    @if (iconFor(provider.provider); as icon) { <i [class]="icon" class="me-2"></i> }
                    {{ nameFor(provider.provider, provider.displayName) }}
                  </button>
                }
              </div>
            }
          }
        </div>
      </bs-card>
    </div>
  `,
})
export class SparkExternalLoginsComponent {
  private readonly auth = inject(SparkAuthService);
  private readonly router = inject(Router);
  private readonly presentations: SparkExternalProviderPresentation[] = inject(SPARK_EXTERNAL_PROVIDERS, { optional: true }) ?? [];
  protected readonly colors = Color;

  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly errorKey = signal('');
  readonly logins = signal<SparkExternalLogins | null>(null);

  constructor() {
    void this.refresh();
  }

  private presentation(scheme: string): SparkExternalProviderPresentation | undefined {
    return this.presentations.find(p => p.scheme.toLowerCase() === scheme.toLowerCase());
  }

  protected iconFor(scheme: string): string | undefined {
    return this.presentation(scheme)?.iconClass;
  }

  protected nameFor(scheme: string, serverName: string): string {
    return this.presentation(scheme)?.displayName ?? serverName ?? scheme;
  }

  async refresh(): Promise<void> {
    this.loading.set(true);
    try {
      this.logins.set(await this.auth.externalLogins());
    } catch {
      // 404 = linking is not enabled on this deployment (SparkExternalLinking disabled).
      this.errorKey.set('auth.externalLoginsUnavailable');
    } finally {
      this.loading.set(false);
    }
  }

  async link(provider: string): Promise<void> {
    this.errorKey.set('');
    this.busy.set(true);
    try {
      const result = await this.auth.linkProvider(provider, { returnUrl: this.router.url });
      if (!result.success && result.error !== 'popup_closed') {
        this.errorKey.set(`auth.externalLoginError.${result.error ?? 'link_failed'}`);
      }
      await this.refresh();
    } finally {
      this.busy.set(false);
    }
  }

  async unlink(provider: string, providerKey: string): Promise<void> {
    this.errorKey.set('');
    this.busy.set(true);
    try {
      const result = await this.auth.unlinkProvider(provider, providerKey);
      if (!result.success) this.errorKey.set(`auth.unlinkError.${result.error ?? 'unlink_failed'}`);
      await this.refresh();
    } finally {
      this.busy.set(false);
    }
  }
}
