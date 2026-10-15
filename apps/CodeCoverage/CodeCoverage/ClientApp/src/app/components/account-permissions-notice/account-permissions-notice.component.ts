import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BrowseService, PendingInstallationPermissions } from '../../services/browse.service';

/** `workflow_run` → `Workflow run`, as GitHub's settings page lists the event. */
function words(name: string): string {
  const text = name.replace(/_/g, ' ').trim();
  return text.length === 0 ? name : text[0].toUpperCase() + text.slice(1);
}

/**
 * "GitHub is waiting for you to accept new permissions" on the Account page (merge-queue PRD O17).
 *
 * When the app asks for more than an installation accepted (the merge queue adds Actions and Workflows),
 * nothing works until someone on the account accepts the upgrade on GitHub. The link is the installation's
 * own `html_url` from the server, never built here: user and organization installations live under
 * different paths. A second link opens the merge queue's setup guide (O13) at its permissions step, for this
 * account, so the guide shows this same notice there; the guide turns that link off (`showGuideLink`).
 *
 * A second message (§0 D95) says what the merge queue needs that the App itself does not request or hear. No
 * installation can accept that; it links the App's own settings page, where its owner adds them.
 *
 * Hidden when nothing is pending or missing, and when the endpoint refuses (401/404: the viewer does not manage the
 * account) or cannot answer, so the notice never stands between a reader and the page.
 */
@Component({
  selector: 'app-account-permissions-notice',
  imports: [RouterLink, BsAlertComponent],
  template: `
    @if (notice(); as n) {
      <bs-alert [type]="warningColor" class="d-block mt-3" data-testid="pending-permissions">
        GitHub is waiting for you to accept new permissions: {{ n.summary }}.
        <a class="alert-link ms-1" [href]="n.htmlUrl" target="_blank" rel="noopener">Review and accept on GitHub</a>
        @if (showGuideLink()) {
          <a class="alert-link ms-2" routerLink="/merge-queue/setup" [queryParams]="{ provider: provider(), account: login() }"
             fragment="permissions" data-testid="merge-queue-setup-link">Merge queue setup guide</a>
        }
      </bs-alert>
    }
    @if (appGaps(); as g) {
      <bs-alert [type]="warningColor" class="d-block mt-3" data-testid="app-permission-gaps">
        The CodeCoverage App itself {{ g.summary }}. This cannot be accepted here: the App's owner adds them under
        Permissions &amp; events on
        @if (g.settingsUrl) {
          <a class="alert-link" [href]="g.settingsUrl" target="_blank" rel="noopener" data-testid="app-settings-link">the App's settings page</a>.
        } @else {
          the App's settings page.
        }
        @if (showGuideLink() && !notice()) {
          <a class="alert-link ms-2" routerLink="/merge-queue/setup" [queryParams]="{ provider: provider(), account: login() }"
             fragment="permissions" data-testid="merge-queue-setup-link">Merge queue setup guide</a>
        }
      </bs-alert>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class AccountPermissionsNoticeComponent {
  private readonly browse = inject(BrowseService);

  readonly provider = input.required<string>();
  readonly login = input.required<string>();
  /** Off on the setup guide itself, where the link would point at the page it is on. */
  readonly showGuideLink = input(true);

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

  /**
   * What the merge queue needs that the App itself does not request or hear (merge-queue PRD §0 D95). Accepting on
   * the installation cannot add those, so this notice points at the App's own settings page instead.
   */
  readonly appGaps = computed(() => {
    const p = this.permissions();
    const permissions = p?.missingFromApp ?? [];
    const events = p?.missingEvents ?? [];
    if (permissions.length === 0 && events.length === 0) return null;
    const parts: string[] = [];
    if (permissions.length > 0)
      parts.push(`does not request: ${permissions.map((permission) => `${permission.displayName} (${permission.declared})`).join(', ')}`);
    if (events.length > 0)
      parts.push(`is not subscribed to: ${events.map(words).join(', ')}`);
    return { summary: parts.join('; and '), settingsUrl: p?.appSettingsUrl ?? null };
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
