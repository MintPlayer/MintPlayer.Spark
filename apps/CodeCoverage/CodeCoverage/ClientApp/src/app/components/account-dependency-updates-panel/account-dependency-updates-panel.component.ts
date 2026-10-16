import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { BsCardBodyComponent, BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { BrowseService, DependencyFleet, DependencyRepoStatus } from '../../services/browse.service';
import { chip, HEALTH, READINESS, repoLink, time } from '../dependency-updates/dependency-health';

/** How many package rows show before "show all". */
export const PACKAGES_SHOWN = 25;

/**
 * The account's "Dependency updates" fleet view (dependency-updates PRD §8, §13.8 N9), on the generic /po Account page
 * through `extraContentTemplate`, below the dependency graph:
 *
 * - **Readiness per platform major**: "Angular 23: 3 of 5 ready", with what blocks the others (the resolver's blockers,
 *   R2) and each repository's status.
 * - **Repositories**: each one's state (CI red, frozen, a run that never answered), its rolling pull request, the last run,
 *   the next slot, and its holds and approvals waiting.
 * - **Packages**: which repository is on which version, and what the bot is doing with it there.
 *
 * Every row comes from the server already filtered like the account's repository list (#453): a private repository the
 * viewer cannot see is in none of them. Hidden when the server refuses, and when nothing on the account uses dependency
 * updates.
 */
@Component({
  selector: 'app-account-dependency-updates-panel',
  imports: [RouterLink, BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent, BsTableComponent],
  template: `
    @if (visible()) {
      @let f = fleet()!;
      <bs-card class="mt-3 d-block" data-testid="dependency-fleet">
        <bs-card-header>
          <div class="d-flex align-items-center">
            <span><i class="bi bi-box-seam"></i> Dependency updates</span>
            <button type="button" class="btn btn-sm btn-link p-0 ms-auto" (click)="load()" aria-label="Refresh the dependency updates">
              <i class="bi bi-arrow-clockwise"></i>
            </button>
          </div>
        </bs-card-header>
        <bs-card-body>
          @if (f.readiness.length > 0) {
            <section class="mb-3" data-testid="du-readiness">
              <h6 class="mb-2">Platform readiness</h6>
              @for (r of f.readiness; track r.platform) {
                <details class="mb-2" [attr.data-platform]="r.platform">
                  <summary>
                    <strong>{{ r.platform }} {{ r.nextMajor }}</strong>: {{ r.ready }} of {{ r.total }} ready
                    @if (r.blockers.length > 0) {
                      <span class="text-muted">
                        — blocked by
                        @for (b of r.blockers; track b.name; let last = $last) {
                          <code>{{ b.name }}</code> ({{ b.repositories }}){{ last ? '' : ', ' }}
                        }
                      </span>
                    }
                  </summary>
                  <ul class="list-unstyled ms-3 mt-2 mb-0">
                    @for (repo of r.repositories; track repo.repositoryId) {
                      <li class="py-1">
                        <a [routerLink]="link(repo.fullName)">{{ repo.fullName }}</a>
                        <span class="small text-muted ms-1">{{ repo.current ?? '—' }}</span>
                        <span [class]="readinessChip(repo.status)" class="ms-2" data-testid="du-readiness-state">{{ readinessLabel(repo.status) }}</span>
                        @if (repo.blockers.length > 0) {
                          <span class="small ms-1">blocked by
                            @for (b of repo.blockers; track b; let last = $last) { <code>{{ b }}</code>{{ last ? '' : ', ' }} }
                          </span>
                        } @else if (repo.detail) {
                          <span class="small text-muted ms-1">{{ repo.detail }}</span>
                        }
                      </li>
                    }
                  </ul>
                </details>
              }
            </section>
          }

          <h6 class="mb-2">Repositories</h6>
          <bs-table [isResponsive]="true" data-testid="du-repositories">
            <thead>
              <tr><th>Repository</th><th>State</th><th>Pull request</th><th>Last run</th><th>Next slot</th><th>Holds</th></tr>
            </thead>
            <tbody>
              @for (repo of f.repositories; track repo.id) {
                <tr [attr.data-repo]="repo.fullName">
                  <td>
                    <a [routerLink]="link(repo.fullName)">{{ repo.name }}</a>
                    @if (repo.isPrivate) { <i class="bi bi-lock ms-1 text-muted" title="Private"></i> }
                  </td>
                  <td><span [class]="healthChip(repo)" data-testid="du-state">{{ healthLabel(repo) }}</span></td>
                  <td>
                    @if (repo.pullRequest; as pr) {
                      @if (pr.url) { <a [href]="pr.url" target="_blank" rel="noopener">#{{ pr.number }}</a> } @else { #{{ pr.number }} }
                      @if (pr.draft) { <span class="small text-muted">draft</span> }
                    } @else { <span class="text-muted">—</span> }
                  </td>
                  <td class="small du-why">
                    {{ time(repo.lastRunAtUtc) }}
                    @if (repo.outcome) { <code class="ms-1">{{ repo.outcome }}</code> }
                    @if (attention(repo) && (repo.onboardingNote || repo.outcomeMessage)) { <div class="text-muted" data-testid="du-why">{{ repo.onboardingNote || repo.outcomeMessage }}</div> }
                  </td>
                  <td class="small">{{ repo.nextSlotUtc ? time(repo.nextSlotUtc) : '—' }}</td>
                  <td class="small du-why">
                    @for (h of repo.holds; track $index) {
                      <div>{{ h.group || h.depNames.join(', ') }}: <span class="text-muted">{{ h.reason }}</span></div>
                    }
                    @for (a of repo.awaitingApproval; track a.group + a.depName) {
                      <div>{{ a.depName }} → {{ a.target }}: <code>{{ a.command }}</code></div>
                    }
                    @if (repo.holds.length === 0 && repo.awaitingApproval.length === 0) { <span class="text-muted">—</span> }
                  </td>
                </tr>
              }
            </tbody>
          </bs-table>

          @if (f.packages.length > 0) {
            <h6 class="mb-2 mt-3">Packages</h6>
            <p class="small text-muted mb-2">Packages used by more than one repository, or with an update going on. The version is the one the lookup found, else the range a manifest declares.</p>
            <bs-table [isResponsive]="true" data-testid="du-packages">
              <thead>
                <tr><th>Package</th><th>Repositories and versions</th></tr>
              </thead>
              <tbody>
                @for (p of shownPackages(); track p.ecosystem + ':' + p.name) {
                  <tr>
                    <td class="du-why"><code>{{ p.name }}</code> <span class="small text-muted">{{ p.ecosystem }}</span></td>
                    <td class="small du-why">
                      @for (u of p.uses; track u.repositoryId + u.path) {
                        <div>
                          {{ u.fullName }} <strong>{{ u.version ?? '—' }}</strong>
                          @if (u.dev) { <span class="text-muted">(dev)</span> }
                          @if (u.status) { <span class="text-muted">— {{ u.status }}</span> }
                        </div>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </bs-table>
            @if (hiddenPackages() > 0) {
              <button type="button" class="btn btn-sm btn-link p-0" (click)="showAll.set(true)">Show {{ hiddenPackages() }} more</button>
            }
            @if (f.packagesTruncated) {
              <p class="small text-muted mb-0">Only the most used packages are listed.</p>
            }
          }
        </bs-card-body>
      </bs-card>
    }
  `,
  styleUrl: './account-dependency-updates-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class AccountDependencyUpdatesPanelComponent {
  private readonly browse = inject(BrowseService);

  /** Canonical forge spelling, e.g. "github". Sourced from the PO's document id (`forgeOf`). */
  readonly provider = input.required<string>();
  readonly login = input.required<string>();

  readonly fleet = signal<DependencyFleet | null>(null);
  readonly showAll = signal(false);

  /** Shown when the account uses dependency updates at all: on for the account, on for a repository, or a run exists. */
  readonly visible = computed(() => {
    const f = this.fleet();
    return !!f && f.repositories.length > 0 && (f.enabled || f.repositories.some(r => r.enabled || r.health !== 'off'));
  });

  readonly shownPackages = computed(() => {
    const packages = this.fleet()?.packages ?? [];
    return this.showAll() ? packages : packages.slice(0, PACKAGES_SHOWN);
  });

  readonly hiddenPackages = computed(() => (this.fleet()?.packages.length ?? 0) - this.shownPackages().length);

  constructor() {
    effect(() => {
      const provider = this.provider();
      const login = this.login();
      void this.load(provider, login);
    });
  }

  async load(provider = this.provider(), login = this.login()): Promise<void> {
    try {
      this.fleet.set(await this.browse.getDependencyFleet(provider, login));
    } catch {
      // 401/404: not this viewer's account to see; anything else: no card rather than a broken one.
      this.fleet.set(null);
    }
  }

  link(fullName: string): string[] {
    return repoLink(this.provider(), fullName);
  }

  healthLabel(repo: DependencyRepoStatus): string {
    return HEALTH[repo.health]?.label ?? repo.health;
  }

  healthChip(repo: DependencyRepoStatus): string {
    return chip(HEALTH[repo.health]?.color ?? 'secondary');
  }

  readinessLabel(status: string): string {
    return READINESS[status]?.label ?? status;
  }

  readinessChip(status: string): string {
    return chip(READINESS[status]?.color ?? 'secondary');
  }

  /** Whether the last run's own words are worth showing: anything but a plain success. */
  attention(repo: DependencyRepoStatus): boolean {
    return !['ok', 'off', 'new', 'running', 'waiting'].includes(repo.health);
  }

  time(utc: string | null): string {
    return time(utc);
  }
}
