import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import {
  SPARK_IDENTITY_PROVIDER_QUERIES,
  SparkIdentityProviderPortalService,
  SparkIdentityProviderTextPipe,
  identityProviderQueryLink,
} from '@mintplayer/ng-spark/identity-provider/core';

/**
 * The target of a mailed team invitation (`/developers/invitations/:token?app=<applicationId>`, PRD
 * D3). Accepting is an explicit click, not a side effect of opening the link, and needs the signed-in
 * account the invitation was sent to; every failure reads the same, as the server answers it.
 */
@Component({
  selector: 'spark-accept-invitation',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, BsAlertComponent, BsCardComponent, BsCardHeaderComponent, BsSpinnerComponent, SparkIdentityProviderTextPipe],
  template: `
    <div class="d-flex justify-content-center">
      <bs-card style="width: 100%; max-width: 640px;">
        <bs-card-header><h3 class="mb-0">{{ 'invite.title' | idp }}</h3></bs-card-header>
        <div class="p-4">
          @if (joined(); as application) {
            <bs-alert [type]="colors.success" class="mb-3 d-block spark-invitation-accepted">{{ 'invite.accepted' | idp: application }}</bs-alert>
            <a class="btn btn-outline-primary spark-invitation-applications" [routerLink]="applicationsLink">{{ 'openApplications' | idp }}</a>
          } @else if (!valid || failed()) {
            <bs-alert [type]="colors.danger" class="mb-3 d-block spark-invitation-invalid">{{ 'invite.invalid' | idp }}</bs-alert>
          } @else {
            <p>{{ 'invite.intro' | idp }}</p>
            <button type="button" class="btn btn-primary spark-invitation-accept" [disabled]="busy()" (click)="accept()">
              @if (busy()) { <bs-spinner class="me-1" /> }
              {{ 'invite.accept' | idp }}
            </button>
          }
        </div>
      </bs-card>
    </div>
  `,
})
export class SparkAcceptInvitationComponent {
  private readonly portal = inject(SparkIdentityProviderPortalService);
  private readonly route = inject(ActivatedRoute);
  protected readonly colors = Color;
  protected readonly applicationsLink = identityProviderQueryLink(SPARK_IDENTITY_PROVIDER_QUERIES.applications);

  private readonly token = this.route.snapshot.paramMap.get('token') ?? '';
  private readonly applicationId = this.route.snapshot.queryParamMap.get('app') ?? '';
  /** A link without both values cannot be an invitation. */
  protected readonly valid = !!this.token && !!this.applicationId;

  readonly busy = signal(false);
  readonly failed = signal(false);
  /** The application joined, once accepted. */
  readonly joined = signal<string | null>(null);

  async accept(): Promise<void> {
    if (!this.valid) return;
    this.busy.set(true);
    const result = await this.portal.acceptInvitation(this.applicationId, this.token);
    if (result.ok && result.value.accepted) {
      this.joined.set(result.value.application || this.applicationId);
    } else {
      this.failed.set(true);
    }
    this.busy.set(false);
  }
}
