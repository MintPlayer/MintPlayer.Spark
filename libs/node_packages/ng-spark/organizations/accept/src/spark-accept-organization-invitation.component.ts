import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
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
  /** Which refusal: `invalid` (any unknown, used or expired link) or `otherAddress` (D43). */
  reason?: 'invalid' | 'otherAddress' | null;
  /**
   * Set only for a join-request link mailed before join requests became approval requests (D26) whose token
   * matches: the app-relative page of that request, where an Owner or Admin decides it. The page goes there.
   */
  approvalRequestUrl?: string | null;
}

/**
 * The target of a mailed organization invitation
 * (`/organizations/accept/:token?id=<invitationId>`, `docs/seo_analyzer_PRD.md` D18). Accepting is an
 * explicit click, never a side effect of opening the link (mail scanners open links). An invitation
 * makes the signed-in account a member. A join-request link mailed before D26 (join requests are approval
 * requests now) goes on to the approval request it became, where an Owner or Admin decides it. Every failure
 * reads the same, except a valid invitation opened by an account that does not own
 * the invited address (D43): the page says so, so the user can sign in with the right account.
 */
@Component({
  selector: 'spark-accept-organization-invitation',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, BsAlertComponent, BsCardComponent, BsCardHeaderComponent, BsSpinnerComponent],
  styleUrl: './spark-accept-organization-invitation.component.scss',
  template: `
    <div class="d-flex justify-content-center">
      <bs-card class="spark-organization-accept-card">
        <bs-card-header><h3 class="mb-0">{{ t('title') }}</h3></bs-card-header>
        <div class="p-4">
          @if (joined(); as joined) {
            <bs-alert [type]="colors.success" class="mb-3 d-block spark-organization-accepted">{{ t('accepted').replace('{0}', joined.organization ?? '') }}</bs-alert>
            <a class="btn btn-outline-primary spark-organization-open" [routerLink]="['/po', 'organization', joined.organizationId]">{{ t('open') }}</a>
          } @else if (!valid || failed()) {
            <bs-alert [type]="colors.danger" class="mb-3 d-block spark-organization-invalid">{{ t(reason()) }}</bs-alert>
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
  private readonly router = inject(Router);
  private readonly translations = inject(SparkAuthTranslationService);
  protected readonly colors = Color;

  private readonly token = this.route.snapshot.paramMap.get('token') ?? '';
  private readonly invitationId = this.route.snapshot.queryParamMap.get('id') ?? '';
  /** A link without both values cannot be an invitation. */
  protected readonly valid = !!this.token && !!this.invitationId;

  readonly busy = signal(false);
  readonly failed = signal(false);
  /** The refusal to show: a translation key under `organizations.spa`. */
  readonly reason = signal<'invalid' | 'otherAddress'>('invalid');
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
      if (result.approvalRequestUrl?.startsWith('/') && !result.approvalRequestUrl.startsWith('//')) {
        await this.router.navigateByUrl(result.approvalRequestUrl);
        return;
      }
      if (result.accepted) this.joined.set(result);
      else {
        this.reason.set(result.reason === 'otherAddress' ? 'otherAddress' : 'invalid');
        this.failed.set(true);
      }
    } catch {
      this.failed.set(true);
    } finally {
      this.busy.set(false);
    }
  }
}
