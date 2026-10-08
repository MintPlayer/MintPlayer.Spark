import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsCheckboxComponent } from '@mintplayer/ng-bootstrap/checkbox';
import { BsFormComponent } from '@mintplayer/ng-bootstrap/form';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { SparkSecretDialogService } from '@mintplayer/ng-spark/client-operations';
import {
  SPARK_IDENTITY_PROVIDER_QUERIES,
  SparkDeveloperStatus,
  SparkIdentityProviderDatePipe,
  SparkIdentityProviderPortalService,
  SparkIdentityProviderTextPipe,
  identityProviderQueryLink,
  injectIdentityProviderText,
} from '@mintplayer/ng-spark/identity-provider/core';

/**
 * The developer portal's landing page (`/developers`, PRD D2/D7): the user's developer status, the
 * terms to accept to request it (again, when they were raised), and for an active developer a link to
 * their applications (the generic query page, members-only filtered) and a one-time registration
 * token for dynamic client registration (RFC 7591), shown in the same dialog as `showSecret`.
 */
@Component({
  selector: 'spark-developers',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink, BsAlertComponent, BsCardComponent, BsCardHeaderComponent, BsCheckboxComponent, BsFormComponent,
    BsSpinnerComponent, SparkIdentityProviderTextPipe, SparkIdentityProviderDatePipe,
  ],
  template: `
    <div class="d-flex justify-content-center">
      <bs-card style="width: 100%; max-width: 760px;">
        <bs-card-header><h3 class="mb-0">{{ 'dev.title' | idp }}</h3></bs-card-header>
        <div class="p-4">
          <p class="text-muted">{{ 'dev.intro' | idp }}</p>
          @if (notice()) {
            <bs-alert [type]="colors.success" class="mb-3 d-block spark-idp-notice">{{ notice() }}</bs-alert>
          }
          @if (error()) {
            <bs-alert [type]="colors.danger" class="mb-3 d-block spark-idp-error">{{ error() }}</bs-alert>
          }
          @if (loading()) {
            <div class="text-center"><bs-spinner /></div>
          } @else if (status(); as s) {
            <p class="fw-semibold spark-developer-status" [attr.data-status]="s.status ?? 'none'">{{ statusKey() | idp }}</p>
            @if (s.requestedAt) { <small class="text-muted d-block">{{ 'dev.requestedAt' | idp: (s.requestedAt | idpDate) }}</small> }
            @if (s.decidedAt) { <small class="text-muted d-block">{{ 'dev.decidedAt' | idp: (s.decidedAt | idpDate) }}</small> }
            @if (s.note) {
              <p class="mt-2 mb-0 spark-developer-note"><span class="text-muted">{{ 'dev.note' | idp }}</span> {{ s.note }}</p>
            }

            @if (canRequest()) {
              <hr class="my-4" />
              @if (termsChanged()) {
                <p class="spark-developer-terms-changed">{{ 'dev.termsChanged' | idp }}</p>
              }
              @if (s.termsUrl) {
                <p><a [href]="s.termsUrl" target="_blank" rel="noopener noreferrer" class="spark-developer-terms">{{ 'dev.readTerms' | idp }}</a></p>
              }
              <bs-form>
                <div class="mb-3">
                  <bs-checkbox [type]="'checkbox'" [name]="'acceptDeveloperTerms'" [isToggled]="accepted()" (isToggledChange)="accepted.set($event === true)">
                    {{ 'dev.acceptTerms' | idp: s.currentTermsVersion }}
                  </bs-checkbox>
                </div>
              </bs-form>
              <button type="button" class="btn btn-primary spark-developer-request" [disabled]="busy() || !accepted()" (click)="request()">
                @if (busy()) { <bs-spinner class="me-1" /> }
                {{ (termsChanged() ? 'dev.acceptAgain' : 'dev.request') | idp }}
              </button>
            }

            @if (s.isActive) {
              <hr class="my-4" />
              <h5>{{ 'dev.applicationsTitle' | idp }}</h5>
              <p class="text-muted">{{ 'dev.applicationsHint' | idp }}</p>
              <a class="btn btn-outline-primary spark-developer-applications" [routerLink]="applicationsLink">{{ 'openApplications' | idp }}</a>

              <h5 class="mt-4">{{ 'dev.registrationTitle' | idp }}</h5>
              <p class="text-muted">{{ 'dev.registrationHint' | idp }}</p>
              <button type="button" class="btn btn-outline-secondary spark-developer-token" [disabled]="busy()" (click)="issueToken()">
                {{ 'dev.issueToken' | idp }}
              </button>
            }
          }
        </div>
      </bs-card>
    </div>
  `,
})
export class SparkDevelopersComponent {
  private readonly text = injectIdentityProviderText();
  private readonly portal = inject(SparkIdentityProviderPortalService);
  private readonly secrets = inject(SparkSecretDialogService);
  protected readonly colors = Color;
  protected readonly applicationsLink = identityProviderQueryLink(SPARK_IDENTITY_PROVIDER_QUERIES.applications);

  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly status = signal<SparkDeveloperStatus | null>(null);
  readonly accepted = signal(false);
  readonly error = signal('');
  readonly notice = signal('');

  protected readonly statusKey = computed(() => {
    const status = this.status()?.status;
    return status ? `dev.status.${status}` : 'dev.status.none';
  });

  /** A developer (or a pending request) whose accepted terms are older than the current ones. */
  protected readonly termsChanged = computed(() => {
    const s = this.status();
    return !!s && (s.status === 'Approved' || s.status === 'Requested') && s.acceptedTermsVersion !== s.currentTermsVersion;
  });

  /** Asking (again) is possible unless an administrator revoked the status; the server refuses that too. */
  protected readonly canRequest = computed(() => {
    const s = this.status();
    if (!s) return false;
    return !s.status || s.status === 'Rejected' || this.termsChanged();
  });

  constructor() {
    void this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    const result = await this.portal.developerStatus();
    if (result.ok) {
      this.status.set(result.value);
    } else {
      this.error.set(this.text('loadFailed'));
    }
    this.accepted.set(false);
    this.loading.set(false);
  }

  async request(): Promise<void> {
    const status = this.status();
    if (!status || !this.accepted()) return;
    this.busy.set(true);
    this.error.set('');
    this.notice.set('');
    // The version the page showed: the server refuses (409) one that was raised in the meantime.
    const result = await this.portal.requestDeveloperStatus(status.currentTermsVersion);
    if (result.ok) {
      this.status.set(result.value);
      this.accepted.set(false);
      this.notice.set(this.text(result.value.isActive ? 'dev.status.Approved' : 'dev.requestSent'));
    } else if (result.status === 409) {
      await this.load();
      this.error.set(this.text('dev.termsChanged'));
    } else if (result.status === 403) {
      this.error.set(this.text('dev.status.Revoked'));
    } else {
      this.error.set(this.text('dev.requestFailed'));
    }
    this.busy.set(false);
  }

  async issueToken(): Promise<void> {
    this.busy.set(true);
    this.error.set('');
    this.notice.set('');
    const result = await this.portal.issueRegistrationToken();
    if (result.ok) {
      const minutes = Math.max(1, Math.round(result.value.expiresIn / 60));
      this.secrets.show({
        title: this.text('dev.tokenTitle'),
        message: this.text('dev.tokenMessage', minutes),
        value: result.value.initialAccessToken,
      });
    } else {
      this.error.set(this.text('dev.tokenFailed'));
    }
    this.busy.set(false);
  }
}
