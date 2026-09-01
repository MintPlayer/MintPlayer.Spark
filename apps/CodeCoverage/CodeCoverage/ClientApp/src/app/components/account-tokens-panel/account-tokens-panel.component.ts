import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsSelectComponent, BsSelectOption } from '@mintplayer/ng-bootstrap/select';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { BrowseService, RepoInfo } from '../../services/browse.service';
import { CreatedToken, TokenInfo, TokensService } from '../../services/tokens.service';

/**
 * Upload-token management for one account, mounted on the generic Account detail page
 * through the poDetail route's `extraContentTemplate`.
 *
 * A panel rather than part of the page, for the same reason the repository panels are:
 * it is form state and an interactive flow (create, copy-once, revoke), which no attribute
 * renderer expresses. Its heading stays a `bs-card-header` — it is a section inside a page,
 * not the page's title, and the page title is now the `<h2>` Spark renders from the
 * Account breadcrumb.
 *
 * Visibility is decided by the server: `GET /api/tokens?account=` answers 403 for an account
 * the viewer cannot manage, and that failure is the gate. No client-side permission guess.
 */
@Component({
  selector: 'app-account-tokens-panel',
  imports: [
    DatePipe, FormsModule,
    BsAlertComponent, BsBadgeComponent, BsCardComponent, BsCardHeaderComponent,
    BsFormComponent, BsFormControlDirective, BsSelectComponent, BsSelectOption, BsTableComponent,
  ],
  template: `
@if (canManage()) {
  <bs-card class="mt-3 d-block">
    <bs-card-header><i class="bi bi-key"></i> Upload tokens</bs-card-header>

    <div class="p-3">
    @if (createdToken(); as created) {
      <bs-alert [type]="successColor" class="d-block mb-3">
        <div class="d-flex align-items-center gap-2 flex-wrap">
          <span>Token created — copy it now, it won't be shown again:</span>
          <code class="user-select-all">{{ created.tokenValue }}</code>
          <button class="btn btn-sm btn-outline-secondary" (click)="copyToken()">
            <i class="bi bi-clipboard"></i> Copy
          </button>
        </div>
      </bs-alert>
    }

    <bs-form (submitted)="createToken()">
      <div class="d-flex align-items-end gap-2 flex-wrap mb-3">
        <div>
          <label class="form-label d-block small mb-1" for="token-description">Description</label>
          <input id="token-description" type="text"
                 [ngModel]="newDescription()" (ngModelChange)="newDescription.set($event)"
                 placeholder="e.g. CI uploads">
        </div>
        <div>
          <label class="form-label d-block small mb-1" for="token-scope">Scope</label>
          <bs-select id="token-scope"
                     [ngModel]="newRepoFullName()" (ngModelChange)="newRepoFullName.set($event)">
            <option [ngValue]="''">All repositories of {{ login() }}</option>
            @for (repo of repos() ?? []; track repo.fullName) {
              <option [ngValue]="repo.fullName">{{ repo.fullName }}</option>
            }
          </bs-select>
        </div>
        <button class="btn btn-sm btn-primary" type="submit" [disabled]="creating()">
          <i class="bi bi-plus-lg"></i> Create token
        </button>
      </div>
    </bs-form>

    @if (tokens(); as list) {
      @if (list.length === 0) {
        <p class="text-muted small mb-0">No upload tokens yet. Create one and pass it to the coverage action as <code>token</code>.</p>
      } @else {
        <!-- See commit-files-panel: the library's responsive wrapper is opt-in. -->
        <bs-table [isResponsive]="true">
          <thead>
            <tr><th>Description</th><th>Scope</th><th>Created</th><th>Status</th><th></th></tr>
          </thead>
          <tbody>
            @for (token of list; track token.id) {
              <tr>
                <td>{{ token.description || '—' }}</td>
                <td class="small">
                  @if (token.scope === 'Repository') {
                    <i class="bi bi-git"></i> {{ token.repositoryFullName || 'repository' }}
                  } @else {
                    <i class="bi bi-people"></i> all of {{ token.accountLogin }}
                  }
                </td>
                <td class="small text-muted">{{ token.createdAtUtc | date:'medium' }}</td>
                <td>
                  @if (token.revokedAtUtc) {
                    <bs-badge class="text-bg-secondary">revoked</bs-badge>
                  } @else {
                    <bs-badge class="text-bg-success">active</bs-badge>
                  }
                </td>
                <td class="text-end">
                  @if (!token.revokedAtUtc) {
                    <button class="btn btn-sm btn-outline-danger" (click)="revokeToken(token)">
                      <i class="bi bi-x-lg"></i> Revoke
                    </button>
                  }
                </td>
              </tr>
            }
          </tbody>
        </bs-table>
      }
    }
    </div>
  </bs-card>
}
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class AccountTokensPanelComponent {
  private readonly browse = inject(BrowseService);
  private readonly tokensService = inject(TokensService);

  /** The account whose tokens these are. Read off the page's PersistentObject. */
  readonly login = input.required<string>();

  /** Fetched for the token-scope dropdown only; authorized viewers are the only ones who see it. */
  readonly repos = signal<RepoInfo[] | null>(null);
  readonly tokens = signal<TokenInfo[] | null>(null);
  readonly canManage = signal(false);
  readonly newDescription = signal('');
  readonly newRepoFullName = signal('');
  readonly createdToken = signal<CreatedToken | null>(null);
  readonly creating = signal(false);

  readonly successColor = Color.success;

  constructor() {
    effect(() => {
      const login = this.login();
      this.repos.set(null);
      this.tokens.set(null);
      this.canManage.set(false);
      this.createdToken.set(null);
      if (!login) return;
      void this.load(login);
    });
  }

  private async load(login: string): Promise<void> {
    // The tokens call decides whether the panel renders at all, so it runs even if
    // the repo list fails — a dropdown with no options beats no panel.
    this.browse.getAccountRepos(login).then(
      (repos) => this.repos.set(repos),
      () => this.repos.set([]));
    await this.loadTokens(login);
  }

  private async loadTokens(login: string): Promise<void> {
    try {
      this.tokens.set(await this.tokensService.list(login));
      this.canManage.set(true);
    } catch {
      this.tokens.set(null);
      this.canManage.set(false);
    }
  }

  async createToken(): Promise<void> {
    this.creating.set(true);
    try {
      const created = await this.tokensService.create(
        this.login(),
        this.newDescription() || null,
        this.newRepoFullName() || null);
      this.createdToken.set(created);
      this.newDescription.set('');
      this.newRepoFullName.set('');
      await this.loadTokens(this.login());
    } finally {
      this.creating.set(false);
    }
  }

  async copyToken(): Promise<void> {
    const created = this.createdToken();
    if (created) await navigator.clipboard.writeText(created.tokenValue);
  }

  async revokeToken(token: TokenInfo): Promise<void> {
    await this.tokensService.revoke(token.id);
    await this.loadTokens(this.login());
  }
}
