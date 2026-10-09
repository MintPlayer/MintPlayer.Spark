import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import {
  SPARK_IDENTITY_PROVIDER_QUERIES,
  SparkIdentityProviderDatePipe,
  SparkIdentityProviderPortalService,
  SparkIdentityProviderTextPipe,
  SparkSigningKey,
  identityProviderQueryLink,
  injectIdentityProviderText,
} from '@mintplayer/ng-spark/identity-provider/core';

/** The library's queues and records, in the order an administrator works through them. */
const QUEUES: { label: string; alias: string }[] = [
  { label: 'query.DeveloperRequests', alias: SPARK_IDENTITY_PROVIDER_QUERIES.developerRequests },
  { label: 'query.ScopeApprovals', alias: SPARK_IDENTITY_PROVIDER_QUERIES.scopeApprovals },
  { label: 'query.GoLiveReviews', alias: SPARK_IDENTITY_PROVIDER_QUERIES.goLiveReviews },
  { label: 'query.Applications', alias: SPARK_IDENTITY_PROVIDER_QUERIES.applications },
  { label: 'query.Resources', alias: SPARK_IDENTITY_PROVIDER_QUERIES.resources },
  { label: 'query.Grants', alias: SPARK_IDENTITY_PROVIDER_QUERIES.grants },
  { label: 'query.AuditEvents', alias: SPARK_IDENTITY_PROVIDER_QUERIES.auditEvents },
];

/**
 * The identity provider's management page (`identity-provider/admin`, PRD D9), for its
 * administrators (`ManageAll/IdentityProvider`): the signing keys with a rotate-now action, and links
 * to the library's queues (developer requests, scope approvals, go-live reviews) and records (grants,
 * the audit trail) in the generic query page. The server answers 403 to anyone else, and the page
 * then says so instead of rendering an empty administration screen.
 */
@Component({
  selector: 'spark-identity-provider-management',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink, BsAlertComponent, BsBadgeComponent, BsCardComponent, BsCardHeaderComponent, BsSpinnerComponent,
    BsTableComponent, SparkIdentityProviderTextPipe, SparkIdentityProviderDatePipe,
  ],
  template: `
    <div class="d-flex justify-content-center">
      <bs-card style="width: 100%; max-width: 960px;">
        <bs-card-header><h3 class="mb-0">{{ 'admin.title' | idp }}</h3></bs-card-header>
        <div class="p-4">
          @if (loading()) {
            <div class="text-center"><bs-spinner /></div>
          } @else if (forbidden()) {
            <bs-alert [type]="colors.warning" class="d-block spark-idp-not-allowed">{{ 'admin.notAllowed' | idp }}</bs-alert>
          } @else {
            @if (notice()) {
              <bs-alert [type]="colors.success" class="mb-3 d-block spark-idp-notice">{{ notice() }}</bs-alert>
            }
            @if (error()) {
              <bs-alert [type]="colors.danger" class="mb-3 d-block spark-idp-error">{{ error() }}</bs-alert>
            }

            <h5>{{ 'admin.queues' | idp }}</h5>
            <ul class="list-unstyled mb-4">
              @for (queue of queues; track queue.alias) {
                <li class="py-1"><a class="spark-idp-queue" [routerLink]="queryLink(queue.alias)">{{ queue.label | idp }}</a></li>
              }
            </ul>

            <h5>{{ 'admin.keys' | idp }}</h5>
            <p class="text-muted">{{ 'admin.keysHint' | idp }}</p>
            @if (keys().length) {
              <bs-table [isResponsive]="true">
                <thead>
                  <tr>
                    <th>{{ 'admin.kid' | idp }}</th>
                    <th>{{ 'admin.algorithm' | idp }}</th>
                    <th>{{ 'admin.state' | idp }}</th>
                    <th>{{ 'admin.created' | idp }}</th>
                    <th>{{ 'admin.activated' | idp }}</th>
                    <th>{{ 'admin.retired' | idp }}</th>
                  </tr>
                </thead>
                <tbody>
                  @for (key of keys(); track key.kid) {
                    <tr class="spark-idp-key" [attr.data-state]="key.state">
                      <td class="font-monospace text-break">{{ key.kid }}</td>
                      <td>{{ key.algorithm }}</td>
                      <td><bs-badge [type]="stateColor(key.state)">{{ key.state }}</bs-badge></td>
                      <td>{{ key.createdAt | idpDate }}</td>
                      <td>{{ key.activatedAt | idpDate }}</td>
                      <td>{{ key.retiredAt | idpDate }}</td>
                    </tr>
                  }
                </tbody>
              </bs-table>
            } @else {
              <p class="text-muted spark-idp-no-keys">{{ 'admin.noKeys' | idp }}</p>
            }
            <button type="button" class="btn btn-outline-danger spark-idp-rotate" [disabled]="busy()" (click)="rotate()">
              @if (busy()) { <bs-spinner class="me-1" /> }
              {{ 'admin.rotate' | idp }}
            </button>
            @if (changes().length) {
              <ul class="small text-muted mt-3 mb-0 spark-idp-rotation-changes">
                @for (change of changes(); track $index) { <li>{{ change }}</li> }
              </ul>
            }
          }
        </div>
      </bs-card>
    </div>
  `,
})
export class SparkIdentityProviderManagementComponent {
  private readonly text = injectIdentityProviderText();
  private readonly portal = inject(SparkIdentityProviderPortalService);
  protected readonly colors = Color;
  protected readonly queues = QUEUES;
  protected readonly queryLink = identityProviderQueryLink;

  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly forbidden = signal(false);
  readonly keys = signal<SparkSigningKey[]>([]);
  readonly changes = signal<string[]>([]);
  readonly error = signal('');
  readonly notice = signal('');

  constructor() {
    void this.load();
  }

  async load(): Promise<void> {
    const result = await this.portal.signingKeys();
    if (result.ok) {
      this.keys.set(result.value);
    } else if (result.status === 403) {
      this.forbidden.set(true);
    } else {
      this.error.set(this.text('loadFailed'));
    }
    this.loading.set(false);
  }

  async rotate(): Promise<void> {
    if (!confirm(this.text('admin.confirmRotate'))) return;
    this.busy.set(true);
    this.error.set('');
    this.notice.set('');
    const result = await this.portal.rotateSigningKeys();
    if (result.ok) {
      this.changes.set(result.value.changes ?? []);
      this.notice.set(this.text('admin.rotated'));
      await this.load();
    } else if (result.status === 403) {
      this.forbidden.set(true);
    } else {
      this.error.set(this.text('admin.rotateFailed'));
    }
    this.busy.set(false);
  }

  protected stateColor(state: string): Color {
    switch (state.toLowerCase()) {
      case 'active': return Color.success;
      case 'retired': return Color.secondary;
      default: return Color.info;
    }
  }
}
