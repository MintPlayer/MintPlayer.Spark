import { ChangeDetectionStrategy, Component, effect, inject, input, signal, computed } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent } from '@mintplayer/ng-bootstrap/card';
import { BsSelectComponent, BsSelectOption } from '@mintplayer/ng-bootstrap/select';
import { BrowseService, RepoInfo } from '../../services/browse.service';

/**
 * "Coverage badge" card for the generic /po Repository detail page: latest
 * coverage, the rendered badge SVG, and — for managers — the README markdown
 * with copy/rotate. Self-fetches RepoInfo so canManage/badgeToken/baseUrl come
 * from the same authority as the vanity page.
 */
@Component({
  selector: 'app-repo-badge-panel',
  imports: [FormsModule, BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent, BsSelectComponent, BsSelectOption],
  template: `
    @if (repo(); as r) {
      <bs-card class="mt-3 d-block">
        <bs-card-header><i class="bi bi-patch-check"></i> Coverage badge</bs-card-header>
        <bs-card-body>
          <div class="d-flex align-items-center gap-3 flex-wrap">
            @if (branches().length > 1) {
              <bs-select id="badge-branch" [size]="'sm'"
                         [ngModel]="selectedBranch()" (ngModelChange)="selectBranch($event)">
                @for (b of branches(); track b) {
                  <option [ngValue]="b">{{ b }}{{ b === r.defaultBranch ? ' (default)' : '' }}</option>
                }
              </bs-select>
            } @else {
              <span class="text-muted">Default branch ({{ r.defaultBranch ?? 'unknown' }}):</span>
            }
            <img [src]="badgeUrl()" alt="coverage badge" height="20">
          </div>

          @if (r.canManage) {
            <div class="border rounded p-2 mt-3 bg-light">
              <div class="d-flex align-items-center gap-2 mb-1">
                <strong class="small">README badge</strong>
                <button class="btn btn-sm btn-outline-secondary" (click)="copyBadge()">
                  <i class="bi bi-clipboard"></i> Copy markdown
                </button>
                @if (r.isPrivate) {
                  <button class="btn btn-sm btn-outline-warning" (click)="rotateBadgeToken()">
                    <i class="bi bi-arrow-repeat"></i> {{ r.badgeToken ? 'Rotate' : 'Create' }} badge token
                  </button>
                }
              </div>
              <code class="small d-block text-break">{{ badgeMarkdown() }}</code>
              @if (r.isPrivate && !r.badgeToken) {
                <div class="small text-muted mt-1">Private repository — create a badge token to make the badge work in your README.</div>
              }
              <div class="small text-muted mt-1">
                Any branch works: add <code>?branch=</code>. For a pull request, add
                <code>?pr={{ '{' }}number{{ '}' }}</code> — it tracks that PR's newest covered commit.
                A branch or PR with no coverage renders an "unknown" badge rather than an error.
              </div>
            </div>
          }
        </bs-card-body>
      </bs-card>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class RepoBadgePanelComponent {
  private readonly browse = inject(BrowseService);

  owner = input.required<string>();
  name = input.required<string>();

  readonly repo = signal<RepoInfo | null>(null);

  /** Branches that actually have coverage; the default branch sorts first. */
  readonly branches = signal<string[]>([]);

  /** Null means "the default branch", which is the parameterless URL. */
  private readonly branchOverride = signal<string | null>(null);

  readonly selectedBranch = computed(() => this.branchOverride() ?? this.repo()?.defaultBranch ?? '');

  /**
   * The parameterless URL when the selection is the default branch, so the
   * snippet every existing README already uses keeps its exact shape; ?branch=
   * only when the user picked something else.
   */
  private badgeQuery(r: RepoInfo): string {
    const params: string[] = [];
    const branch = this.selectedBranch();
    if (branch && branch !== r.defaultBranch) params.push(`branch=${encodeURIComponent(branch)}`);
    // Token last: a copied URL reads more legibly with the selector first.
    if (r.isPrivate && r.badgeToken) params.push(`token=${encodeURIComponent(r.badgeToken)}`);
    return params.length ? `?${params.join('&')}` : '';
  }

  readonly badgeUrl = computed(() => {
    const r = this.repo();
    if (!r) return '';
    return `/badge/${r.owner}/${r.name}.svg${this.badgeQuery(r)}`;
  });

  readonly badgeMarkdown = computed(() => {
    const r = this.repo();
    if (!r) return '';
    const origin = r.baseUrl || location.origin;
    const url = `${origin}/badge/${r.owner}/${r.name}.svg${this.badgeQuery(r)}`;
    return `[![Coverage](${url})](${origin}/r/${r.owner}/${r.name})`;
  });

  constructor() {
    effect(async () => {
      const owner = this.owner();
      const name = this.name();
      try {
        this.repo.set(await this.browse.getRepo(owner, name));
      } catch {
        this.repo.set(null);
      }

      // Independent of the repo fetch: a failure here costs the picker, not
      // the badge, so it must not null out the panel.
      try {
        this.branches.set(await this.browse.getBranches(owner, name));
      } catch {
        this.branches.set([]);
      }
    });
  }

  selectBranch(branch: string): void {
    this.branchOverride.set(branch);
  }

  async copyBadge(): Promise<void> {
    await navigator.clipboard.writeText(this.badgeMarkdown());
  }

  async rotateBadgeToken(): Promise<void> {
    const r = this.repo();
    if (!r) return;
    const result = await this.browse.rotateBadgeToken(r.owner, r.name);
    this.repo.set({ ...r, badgeToken: result.badgeToken });
  }
}
