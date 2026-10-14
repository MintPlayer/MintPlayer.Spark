import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BrowseService, PendingInstallationPermissions } from '../../services/browse.service';

/**
 * "GitHub is waiting for you to accept new permissions" on the Account page (merge-queue PRD O17).
 *
 * When the app asks for more than an installation accepted (the merge queue adds Actions and Workflows),
 * nothing works until someone on the account accepts the upgrade on GitHub. The link is the installation's
 * own `html_url` from the server, never built here: user and organization installations live under
 * different paths.
 *
 * Hidden when nothing is pending, and when the endpoint refuses (401/404: the viewer does not manage the
 * account) or cannot answer, so the notice never stands between a reader and the page.
 */
@Component({
  selector: 'app-account-permissions-notice',
  imports: [BsAlertComponent],
  template: `
    @if (notice(); as n) {
      <bs-alert [type]="warningColor" class="d-block mt-3" data-testid="pending-permissions">
        GitHub is waiting for you to accept new permissions: {{ n.summary }}.
        <a class="alert-link ms-1" [href]="n.htmlUrl" target="_blank" rel="noopener">Review and accept on GitHub</a>
      </bs-alert>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class AccountPermissionsNoticeComponent {
  private readonly browse = inject(BrowseService);

  readonly provider = input.required<string>();
  readonly login = input.required<string>();

  readonly warningColor = Color.warning;

  private readonly permissions = signal<PendingInstallationPermissions | null>(null);

  readonly notice = computed(() => {
    const p = this.permissions();
    if (!p || p.pending.length === 0) return null;
    return {
      htmlUrl: p.htmlUrl,
      summary: p.pending.map((permission) => `${permission.displayName} (${permission.declared})`).join(', '),
    };
  });

  constructor() {
    effect(async () => {
      const provider = this.provider();
      const login = this.login();
      this.permissions.set(null);
      try {
        this.permissions.set(await this.browse.getPendingPermissions(provider, login));
      } catch {
        // 401/404 (not a manager of this account) and transport errors alike: no notice.
        this.permissions.set(null);
      }
    });
  }
}
