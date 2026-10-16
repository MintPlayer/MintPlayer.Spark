import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { BsCardBodyComponent, BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { BrowseService, DependencyRepoView } from '../../services/browse.service';
import { chip, HEALTH, time } from '../dependency-updates/dependency-health';

/**
 * A repository's dependency updates on the generic /po Repository detail page (dependency-updates PRD §13.8 N9): the
 * rolling pull request, how the last run ended, the next scheduled slot, what is held and why, what waits for an
 * `approve`, what a fix on the branch keeps pinned, and what the current pull request applies. Read-only for everyone who
 * may browse the repository; the commands are comments on the pull request.
 *
 * Hidden when the server refuses (not this viewer's repository), and when dependency updates are off and never ran here.
 */
@Component({
  selector: 'app-repo-dependency-updates-panel',
  imports: [BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent, BsTableComponent],
  template: `
    @if (visible()) {
      @let v = view()!;
      @let s = v.status;
      <bs-card class="mt-3 d-block" data-testid="dependency-updates">
        <bs-card-header>
          <div class="d-flex align-items-center">
            <span><i class="bi bi-box-seam"></i> Dependency updates</span>
            <button type="button" class="btn btn-sm btn-link p-0 ms-auto" (click)="load()" aria-label="Refresh the dependency updates">
              <i class="bi bi-arrow-clockwise"></i>
            </button>
          </div>
        </bs-card-header>
        <bs-card-body>
          <div class="d-flex flex-wrap align-items-center gap-2 mb-2" data-testid="du-summary">
            <span [class]="healthChip()" data-testid="du-state">{{ healthLabel() }}</span>
            @if (s.pullRequest; as pr) {
              @if (pr.url) {
                <a [href]="pr.url" target="_blank" rel="noopener" data-testid="du-pr">Pull request #{{ pr.number }}</a>
              } @else {
                <span data-testid="du-pr">Pull request #{{ pr.number }}</span>
              }
              @if (pr.draft) { <span class="small text-muted">draft</span> }
            }
            @if (s.plannedGroups > 0) { <span class="small text-muted">{{ s.plannedGroups }} group(s) applied</span> }
          </div>

          <dl class="du-facts small mb-2">
            <dt>Last run</dt>
            <dd>
              {{ time(s.lastRunAtUtc) }}
              @if (s.outcome) { <code class="ms-1">{{ s.outcome }}</code> }
              @if (s.outcomeMessage) { <div class="text-muted du-why">{{ s.outcomeMessage }}</div> }
            </dd>
            <dt>Next slot</dt>
            <dd data-testid="du-next">
              @if (s.nextSlotUtc) { {{ time(s.nextSlotUtc) }} } @else { — }
              @if (s.schedule) { <span class="text-muted">({{ s.schedule }})</span> }
            </dd>
            @if (s.onboardingNote) {
              <dt>Onboarding</dt>
              <dd class="du-why" data-testid="du-onboarding">{{ s.onboardingNote }}</dd>
            }
            @if (s.coverageNote) {
              <dt>Coverage</dt>
              <dd>{{ s.coverageNote }}</dd>
            }
          </dl>

          @if (s.holds.length > 0) {
            <h6 class="mt-3 mb-1">Held</h6>
            <ul class="small mb-2" data-testid="du-holds">
              @for (h of s.holds; track $index) {
                <li class="du-why">{{ h.group || h.depNames.join(', ') }}: {{ h.reason }}</li>
              }
            </ul>
          }

          @if (s.awaitingApproval.length > 0) {
            <h6 class="mt-3 mb-1">Awaiting approval</h6>
            <p class="small text-muted mb-1">A writer comments the line on the pull request to take the update in.</p>
            <ul class="small mb-2" data-testid="du-awaiting">
              @for (a of s.awaitingApproval; track a.group + a.depName) {
                <li>{{ a.depName }} {{ a.current ?? '—' }} → {{ a.target ?? '—' }}: <code>{{ a.command }}</code></li>
              }
            </ul>
          }

          @if (s.pins.length > 0) {
            <h6 class="mt-3 mb-1">Pinned for a fix on the branch</h6>
            <ul class="small mb-2" data-testid="du-pins">
              @for (p of s.pins; track p) { <li>{{ p }}</li> }
            </ul>
          }

          @if (v.planned.length > 0) {
            <bs-table [isResponsive]="true" data-testid="du-planned">
              <thead>
                <tr><th>Group</th><th>Dependency</th><th>File</th><th>From</th><th>To</th></tr>
              </thead>
              <tbody>
                @for (g of v.planned; track g.group) {
                  @for (i of g.items; track i.depName + i.packageFile) {
                    <tr>
                      <td>{{ g.group }} <span class="small text-muted">{{ g.level }}{{ g.autoMerge ? '' : ', needs a person' }}</span></td>
                      <td><code>{{ i.depName }}</code></td>
                      <td class="small">{{ i.packageFile }}</td>
                      <td>{{ i.from }}</td>
                      <td>{{ i.to }}</td>
                    </tr>
                  }
                }
              </tbody>
            </bs-table>
          }

          @if (v.history.length > 0) {
            <details class="mt-2" data-testid="du-history">
              <summary class="small">Recent steps</summary>
              <ul class="list-unstyled small mb-0 mt-1">
                @for (e of v.history; track $index) {
                  <li class="du-why"><span class="text-muted">{{ time(e.atUtc) }}</span> {{ e.text }}</li>
                }
              </ul>
            </details>
          }
        </bs-card-body>
      </bs-card>
    }
  `,
  styleUrl: './repo-dependency-updates-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class RepoDependencyUpdatesPanelComponent {
  private readonly browse = inject(BrowseService);

  /** Canonical forge spelling, e.g. "github". Sourced from the PO's document id (`forgeOf`). */
  readonly provider = input.required<string>();
  readonly owner = input.required<string>();
  readonly name = input.required<string>();

  readonly view = signal<DependencyRepoView | null>(null);

  /** Off and never run: nothing to show. */
  readonly visible = computed(() => {
    const v = this.view();
    return !!v && (v.status.enabled || v.status.outcome !== null || v.status.pullRequest !== null);
  });

  readonly healthLabel = computed(() => {
    const health = this.view()?.status.health ?? 'off';
    return HEALTH[health]?.label ?? health;
  });

  readonly healthChip = computed(() => chip(HEALTH[this.view()?.status.health ?? 'off']?.color ?? 'secondary'));

  constructor() {
    effect(() => {
      const provider = this.provider();
      const owner = this.owner();
      const name = this.name();
      void this.load(provider, owner, name);
    });
  }

  async load(provider = this.provider(), owner = this.owner(), name = this.name()): Promise<void> {
    try {
      this.view.set(await this.browse.getRepoDependencyUpdates(provider, owner, name));
    } catch {
      // 401/404: not this viewer's repository to see; anything else: no panel rather than a broken one.
      this.view.set(null);
    }
  }

  time(utc: string | null): string {
    return time(utc);
  }
}
