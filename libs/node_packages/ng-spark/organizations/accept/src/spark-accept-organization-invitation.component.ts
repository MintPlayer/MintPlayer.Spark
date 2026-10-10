import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { SparkAuthTranslationService } from '@mintplayer/ng-spark/auth/core';

/** What `POST /spark/organizations/accept` answers. */
export interface SparkOrganizationAcceptance {
  accepted: boolean;
  organizationId: string | null;
  organization: string | null;
  problem: string | null;
}

/**
 * The target of a mailed organization invitation or join request
 * (`/organizations/accept/:token?id=<invitationId>`, `docs/seo_analyzer_PRD.md` D18). Accepting is an
 * explicit click, never a side effect of opening the link (mail scanners open links). An invitation
 * makes the signed-in account a member; a join request lets its requester in and needs an Owner or
 * Admin. Every failure reads the same, as the server answers it.
 */
@Component({
  selector: 'spark-accept-organization-invitation',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, BsAlertComponent, BsCardComponent, BsCardHeaderComponent, BsSpinnerComponent],
  template: `
    <div class="d-flex justify-content-center">
      <bs-card style="width: 100%; max-width: 640px;">
        <bs-card-header><h3 class="mb-0">{{ t('title') }}</h3></bs-card-header>
        <div class="p-4">
          @if (joined(); as joined) {
            <bs-alert [type]="colors.success" class="mb-3 d-block spark-organization-accepted">{{ t('accepted').replace('{0}', joined.organization ?? '') }}</bs-alert>
            <a class="btn btn-outline-primary spark-organization-open" [routerLink]="['/po', 'organization', joined.organizationId]">{{ t('open') }}</a>
          } @else if (!valid || failed()) {
            <bs-alert [type]="colors.danger" class="mb-3 d-block spark-organization-invalid">{{ t('invalid') }}</bs-alert>
          } @else {
            <p>{{ t('intro') }}</p>
            <button type="button" class="btn btn-primary spark-organization-accept" [disabled]="busy()" (click)="accept()">
              @if (busy()) { <bs-spinner class="me-1" /> }
              {{ t('accept') }}
            </button>
          }
        </div>
      </bs-card>
    </div>
  `,
})
export class SparkAcceptOrganizationInvitationComponent {
  private readonly http = inject(HttpClient);
  private readonly route = inject(ActivatedRoute);
  private readonly translations = inject(SparkAuthTranslationService);
  protected readonly colors = Color;

  private readonly token = this.route.snapshot.paramMap.get('token') ?? '';
  private readonly invitationId = this.route.snapshot.queryParamMap.get('id') ?? '';
  /** A link without both values cannot be an invitation. */
  protected readonly valid = !!this.token && !!this.invitationId;

  readonly busy = signal(false);
  readonly failed = signal(false);
  /** The organization joined, once accepted. */
  readonly joined = signal<SparkOrganizationAcceptance | null>(null);

  /** The page's texts: `organizations.spa.*` in the organizations library's `translations.json`. */
  protected t(key: string): string {
    return this.translations.t('organizations.spa.' + key);
  }

  async accept(): Promise<void> {
    if (!this.valid) return;
    this.busy.set(true);
    try {
      const result = await firstValueFrom(this.http.post<SparkOrganizationAcceptance>('/spark/organizations/accept', {
        invitationId: this.invitationId,
        token: this.token,
      }));
      if (result.accepted) this.joined.set(result);
      else this.failed.set(true);
    } catch {
      this.failed.set(true);
    } finally {
      this.busy.set(false);
    }
  }
}
