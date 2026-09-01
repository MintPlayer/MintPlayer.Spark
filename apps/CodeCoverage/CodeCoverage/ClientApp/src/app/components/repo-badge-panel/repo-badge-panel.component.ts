import { ChangeDetectionStrategy, Component, effect, inject, input, signal, computed } from '@angular/core';
import { BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent } from '@mintplayer/ng-bootstrap/card';
import { BrowseService, RepoInfo } from '../../services/browse.service';

/**
 * "Coverage badge" card for the generic /po Repository detail page: latest
 * coverage, the rendered badge SVG, and — for managers — the README markdown
 * with copy/rotate. Self-fetches RepoInfo so canManage/badgeToken/baseUrl come
 * from the same authority as the vanity page.
 */
@Component({
  selector: 'app-repo-badge-panel',
  imports: [BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent],
  template: `
    @if (repo(); as r) {
      <bs-card class="mt-3 d-block">
        <bs-card-header><i class="bi bi-patch-check"></i> Coverage badge</bs-card-header>
        <bs-card-body>
          <div class="d-flex align-items-center gap-3">
            <span class="text-muted">Default branch ({{ r.defaultBranch ?? 'unknown' }}):</span>
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

  readonly badgeUrl = computed(() => {
    const r = this.repo();
    if (!r) return '';
    const base = `/badge/${r.owner}/${r.name}.svg`;
    return r.isPrivate && r.badgeToken ? `${base}?token=${r.badgeToken}` : base;
  });

  readonly badgeMarkdown = computed(() => {
    const r = this.repo();
    if (!r) return '';
    const origin = r.baseUrl || location.origin;
    const base = `${origin}/badge/${r.owner}/${r.name}.svg`;
    const url = r.isPrivate && r.badgeToken ? `${base}?token=${r.badgeToken}` : base;
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
    });
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
