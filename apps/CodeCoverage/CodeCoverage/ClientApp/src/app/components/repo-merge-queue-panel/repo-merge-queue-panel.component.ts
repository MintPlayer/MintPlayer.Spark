import { ChangeDetectionStrategy, Component, computed, DestroyRef, effect, inject, input, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsCardBodyComponent, BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsSelectComponent, BsSelectOption } from '@mintplayer/ng-bootstrap/select';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { BrowseService, MergeQueueCandidates, MergeQueueCommandAccepted, MergeQueueEntry, MergeQueueView } from '../../services/browse.service';

/** GitHub's repository roles, weakest first; queueing needs `write` (merge-queue PRD O9). */
const ROLES = ['none', 'read', 'triage', 'write', 'maintain', 'admin'];

/** The saga's states as a reader would say them, with the theme colour of their chip. */
const STATES: Record<string, { label: string; color: string }> = {
  Queued: { label: 'waiting', color: 'secondary' },
  Preparing: { label: 'rebasing', color: 'info' },
  Regenerating: { label: 'regenerating', color: 'info' },
  Publishing: { label: 'publishing', color: 'info' },
  AwaitingChecks: { label: 'waiting for checks', color: 'primary' },
  Merging: { label: 'merging', color: 'primary' },
  Merged: { label: 'merged', color: 'success' },
  Evicted: { label: 'evicted', color: 'danger' },
  Dequeued: { label: 'dequeued', color: 'secondary' },
  Refused: { label: 'not queued', color: 'warning' },
};

/**
 * A state chip's classes: Bootstrap's subtle background with its emphasis text and border, a pair made for both themes
 * (merge-queue PRD §0 D75, D84). Every state reads at 7.15:1 or more in both themes; `<bs-badge>`'s solid colours
 * (`text-bg-*` since ng-bootstrap 22.25) go down to 4.5:1 on primary, success and danger.
 * Not a bare `.badge`: ng-bootstrap styles `.badge` only inside `<bs-badge>`, so `.mq-state` carries the shape itself.
 */
function chip(color: string): string {
  return `mq-state bg-${color}-subtle text-${color}-emphasis border border-${color}-subtle`;
}

const DATE_TIME = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' });

/** The queue answers a command asynchronously; the panel looks again after this long. */
export const RELOAD_AFTER_COMMAND_MS = 2500;

/**
 * The merge queue on the generic /po Repository detail page (merge-queue PRD M6, O8, O15).
 *
 * - **Read-only for everyone who may browse the repository:** per base branch, the pull request being worked on, the
 *   ones waiting in order, and how the latest runs ended; plus the configuration a run used, each value with its source.
 * - **The button** (a pull request picked from the open ones not yet queued, and "Queue"; "Dequeue" on an entry) raises the same command a `merge-queue` label or a
 *   `/queue` comment raises, as the signed-in user's own GitHub login. The queue checks the role itself (O9), so the role
 *   the server reports here only decides which buttons make sense; an unknown role offers them and lets the queue decide.
 * - **The setup guide** (/merge-queue/setup, O13) is linked from the header for a GitHub repository, for its account, so
 *   the guide can show that account's pending-permissions notice.
 *
 * Hidden when the forge has no queue, when the server refuses (the repository is not this viewer's to see), and for an
 * anonymous visitor when nothing has gone through the queue.
 */
@Component({
  selector: 'app-repo-merge-queue-panel',
  imports: [FormsModule, RouterLink, BsAlertComponent, BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent, BsSelectComponent, BsSelectOption, BsTableComponent],
  template: `
    @if (visible()) {
      @let v = view()!;
      <bs-card class="mt-3 d-block" data-testid="merge-queue">
        <bs-card-header>
          <div class="d-flex align-items-center">
            <span><i class="bi bi-signpost-split"></i> Merge queue</span>
            <span class="ms-auto d-flex align-items-center gap-3">
              @if (setupGuide(); as guide) {
                <a class="small" routerLink="/merge-queue/setup" [queryParams]="guide" data-testid="mq-setup-link">
                  <i class="bi bi-book"></i> Setup guide
                </a>
              }
              <button type="button" class="btn btn-sm btn-link p-0" (click)="refresh()" aria-label="Refresh the merge queue">
                <i class="bi bi-arrow-clockwise"></i>
              </button>
            </span>
          </div>
        </bs-card-header>
        <bs-card-body>
          @if (notice(); as n) {
            <bs-alert [type]="n.color" class="d-block mb-3" data-testid="mq-notice">{{ n.text }}</bs-alert>
          }

          @for (lane of v.lanes; track lane.baseBranch) {
            <section class="mb-3" [attr.data-lane]="lane.baseBranch">
              <h6 class="mb-2">
                <i class="bi bi-arrow-right-short"></i> {{ lane.baseBranch }}
                @if (lane.paused) {
                  <span class="mq-state bg-secondary-subtle text-secondary-emphasis border border-secondary-subtle ms-1" data-testid="mq-state">paused</span>
                }
              </h6>

              @if (lane.current; as c) {
                <div class="d-flex flex-wrap align-items-center gap-2" data-testid="mq-current">
                  <strong>#{{ c.number }}</strong>
                  <span>{{ c.title }}</span>
                  <span [class]="badge(c.state)" data-testid="mq-state">{{ label(c.state) }}</span>
                  <span class="small text-muted">since {{ time(c.sinceUtc) }}</span>
                  @if (c.runUrl) {
                    <a class="small" [href]="c.runUrl" target="_blank" rel="noopener">regenerate run</a>
                  }
                  @if (canDequeue(c)) {
                    <button type="button" class="btn btn-sm btn-outline-danger ms-auto" [disabled]="busy()" (click)="dequeue(c)">Dequeue</button>
                  }
                </div>
                @if (c.step) {
                  <div class="small text-muted mb-2">{{ c.step }}</div>
                }
              } @else {
                <p class="small text-muted mb-2">Nothing is running on {{ lane.baseBranch }}.</p>
              }

              @if (lane.waiting.length > 0) {
                <ol class="list-unstyled mb-2" data-testid="mq-waiting">
                  @for (w of lane.waiting; track w.number) {
                    <li class="d-flex flex-wrap align-items-center gap-2 py-1">
                      <span class="small text-muted">{{ w.position }}.</span>
                      <strong>#{{ w.number }}</strong>
                      <span>{{ w.title }}</span>
                      <span class="small text-muted">queued by &#64;{{ w.enqueuedBy }}, {{ time(w.enqueuedAtUtc) }}</span>
                      @if (canDequeue(w)) {
                        <button type="button" class="btn btn-sm btn-outline-danger ms-auto" [disabled]="busy()" (click)="dequeue(w)">Dequeue</button>
                      }
                    </li>
                  }
                </ol>
              }

              @if (lane.recent.length > 0) {
                <bs-table [isResponsive]="true" data-testid="mq-recent">
                  <thead>
                    <tr><th>Pull request</th><th>Outcome</th><th>Why</th><th class="text-end">When</th></tr>
                  </thead>
                  <tbody>
                    @for (o of lane.recent; track o.number + '/' + o.run) {
                      <tr>
                        <td><strong>#{{ o.number }}</strong> {{ o.title }}</td>
                        <td><span [class]="badge(o.state)" data-testid="mq-state">{{ label(o.state) }}</span></td>
                        <td class="small mq-why">
                          @if (o.reason) { <code>{{ o.reason }}</code> }
                          @if (o.message) { <div class="text-muted">{{ o.message }}</div> }
                        </td>
                        <td class="small text-muted text-end">{{ time(o.finishedAtUtc) }}</td>
                      </tr>
                    }
                  </tbody>
                </bs-table>
              }
            </section>
          } @empty {
            <p class="text-muted">
              Nothing has gone through the queue yet. It takes pull requests once <code>coverage.yml</code> on the default
              branch says <code>mergeQueue: enabled: true</code>.
            </p>
          }

          @if (v.config; as config) {
            <details class="mt-2" data-testid="mq-config">
              <summary class="small">
                Effective configuration, as the run of #{{ config.number }} read it
                @if (config.configRef) { (<code>coverage.yml</code> on {{ config.configRef }}) }
              </summary>
              <bs-table [isResponsive]="true">
                <thead>
                  <tr><th>Setting</th><th>Value</th><th>Source</th></tr>
                </thead>
                <tbody>
                  @for (value of config.values; track value.key) {
                    <tr>
                      <td><code>{{ value.key }}</code></td>
                      <td>{{ value.value }}</td>
                      <td class="small text-muted">{{ value.source }}</td>
                    </tr>
                  }
                </tbody>
              </bs-table>
            </details>
          }

          @if (canQueue()) {
            <div class="d-flex flex-wrap align-items-center gap-2 mt-3" data-testid="mq-enqueue">
              @if (candidateList().length > 0) {
                <bs-select class="mq-candidates" [size]="'sm'" aria-label="Pull request to queue" data-testid="mq-candidates"
                           [ngModel]="number()" (ngModelChange)="number.set($event)">
                  <option [ngValue]="null">Choose a pull request</option>
                  @for (pr of candidateList(); track pr.number) {
                    <option [ngValue]="pr.number">#{{ pr.number }} {{ pr.title }}</option>
                  }
                </bs-select>
              } @else if (candidates()) {
                <span class="small text-muted" data-testid="mq-no-candidates">{{ noCandidates() }}</span>
              }
              <button type="button" class="btn btn-sm btn-primary" [disabled]="busy() || candidateList().length === 0" (click)="queue()">
                <i class="bi bi-signpost-split"></i> Queue as &#64;{{ viewer()?.login }}
              </button>
            </div>
          } @else if (hint(); as h) {
            <p class="small text-muted mt-3 mb-0" data-testid="mq-hint">{{ h }}</p>
          }
        </bs-card-body>
      </bs-card>
    }
  `,
  styleUrl: './repo-merge-queue-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class RepoMergeQueuePanelComponent {
  private readonly browse = inject(BrowseService);
  private readonly destroyRef = inject(DestroyRef);

  /** Canonical forge spelling, e.g. "github". Sourced from the PO's document id (`forgeOf`). */
  readonly provider = input.required<string>();
  readonly owner = input.required<string>();
  readonly name = input.required<string>();

  readonly view = signal<MergeQueueView | null>(null);
  /** The pull requests the button may queue; null until fetched, and for a viewer who has no button. */
  readonly candidates = signal<MergeQueueCandidates | null>(null);
  /** The pull request picked in the dropdown. */
  readonly number = signal<number | null>(null);
  readonly busy = signal(false);
  readonly notice = signal<{ color: Color; text: string } | null>(null);

  readonly viewer = computed(() => this.view()?.viewer ?? null);

  /** Whether the viewer's role allows queueing; an unknown role does, and the queue decides (O9). */
  private readonly mayWrite = computed(() => {
    const role = this.viewer()?.role;
    return role == null || ROLES.indexOf(role) >= ROLES.indexOf('write');
  });

  readonly canQueue = computed(() => !!this.viewer()?.login && this.mayWrite());

  readonly candidateList = computed(() => this.candidates()?.pullRequests ?? []);

  /** Why the dropdown is missing: the server's reason when it gave one. */
  readonly noCandidates = computed(() => this.candidates()?.unavailable || 'No open pull request is waiting to be queued.');

  /** Why a signed-in viewer has no button. Nothing for an anonymous visitor, who reads the queue only. */
  readonly hint = computed(() => {
    const viewer = this.viewer();
    if (!viewer?.signedIn) return null;
    if (!viewer.linked)
      return 'Your account has no linked GitHub sign-in. The queue acts as your GitHub login, so sign in with GitHub to queue pull requests from here.';
    if (!viewer.login)
      return viewer.credential === 'Unavailable'
        ? 'GitHub could not confirm who you are right now; try again in a minute.'
        : 'Your GitHub sign-in has expired. Sign in with GitHub again to queue pull requests from here.';
    return `Queueing needs write access to ${this.owner()}/${this.name()} on GitHub, and you have ${viewer.role} access. The author of a queued pull request can still take it out.`;
  });

  readonly visible = computed(() => {
    const v = this.view();
    return !!v && v.supported && (v.lanes.length > 0 || v.viewer.signedIn);
  });

  /** The setup guide's query for this repository's account. The guide is GitHub's, so other forges get no link. */
  readonly setupGuide = computed(() => this.provider() === 'github' ? { provider: this.provider(), account: this.owner() } : null);

  private reload: ReturnType<typeof setTimeout> | undefined;

  constructor() {
    effect(async () => {
      const provider = this.provider();
      const owner = this.owner();
      const name = this.name();
      this.notice.set(null);
      await this.load(provider, owner, name);
    });
    this.destroyRef.onDestroy(() => clearTimeout(this.reload));
  }

  /**
   * The queue, then, for a viewer with the button, the pull requests it may queue. `refreshCandidates` asks the server to
   * skip its remembered forge answer (the refresh button); the queue's own state is always read live.
   */
  async load(provider = this.provider(), owner = this.owner(), name = this.name(), refreshCandidates = false): Promise<void> {
    try {
      this.view.set(await this.browse.getMergeQueue(provider, owner, name));
    } catch {
      // 401/404: not this viewer's repository to see; anything else: no panel rather than a broken one.
      this.view.set(null);
    }
    await this.loadCandidates(provider, owner, name, refreshCandidates);
  }

  /** The header's refresh button: the queue, and the open pull requests fresh from the forge. */
  refresh(): Promise<void> {
    return this.load(this.provider(), this.owner(), this.name(), true);
  }

  private async loadCandidates(provider: string, owner: string, name: string, refresh: boolean): Promise<void> {
    // Only a viewer with the button needs the list, and only they may read it.
    if (!this.canQueue()) {
      this.candidates.set(null);
      this.number.set(null);
      return;
    }
    let candidates: MergeQueueCandidates;
    try {
      candidates = await this.browse.getQueueCandidates(provider, owner, name, refresh);
    } catch {
      candidates = { pullRequests: [], unavailable: 'The open pull requests could not be listed right now; try again in a minute.' };
    }
    this.candidates.set(candidates);
    // A pick that left the list (queued meanwhile, closed) is no pick.
    const picked = this.number();
    if (picked !== null && !candidates.pullRequests.some(pr => pr.number === picked)) this.number.set(null);
  }

  canDequeue(entry: MergeQueueEntry): boolean {
    const login = this.viewer()?.login;
    return !!login && (this.mayWrite() || login.toLowerCase() === entry.authorLogin?.toLowerCase());
  }

  async queue(): Promise<void> {
    const number = this.number();
    if (!number || !Number.isInteger(number) || number < 1) {
      this.notice.set({ color: Color.warning, text: 'Choose a pull request to queue.' });
      return;
    }
    if (await this.send(() => this.browse.enqueuePullRequest(this.provider(), this.owner(), this.name(), number),
      accepted => `Asked the queue to take #${accepted.number} as @${accepted.actor}. It answers on the pull request as well; a refusal shows up here.`))
      this.number.set(null);
  }

  async dequeue(entry: MergeQueueEntry): Promise<void> {
    await this.send(() => this.browse.dequeuePullRequest(this.provider(), this.owner(), this.name(), entry.number),
      accepted => `Asked the queue to let #${accepted.number} go, as @${accepted.actor}.`);
  }

  label(state: string): string {
    return STATES[state]?.label ?? state;
  }

  badge(state: string): string {
    return chip(STATES[state]?.color ?? 'secondary');
  }

  /** The server's times are UTC; one without a zone designator is read as UTC rather than as local time. */
  time(utc: string): string {
    const date = new Date(/[zZ]|[+-]\d\d:?\d\d$/.test(utc) ? utc : `${utc}Z`);
    return Number.isNaN(date.getTime()) ? utc : DATE_TIME.format(date);
  }

  private async send(call: () => Promise<MergeQueueCommandAccepted>, success: (accepted: MergeQueueCommandAccepted) => string): Promise<boolean> {
    this.busy.set(true);
    try {
      const accepted = await call();
      this.notice.set({ color: Color.success, text: success(accepted) });
      await this.load();
      // The queue takes the command on its own lane; look again once it has.
      clearTimeout(this.reload);
      this.reload = setTimeout(() => void this.load(), RELOAD_AFTER_COMMAND_MS);
      return true;
    } catch (error) {
      this.notice.set({ color: Color.danger, text: refusal(error) });
      return false;
    } finally {
      this.busy.set(false);
    }
  }
}

/** The server's own words for a refused command (`{ error }`), else a plain fallback. */
function refusal(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    const body = error.error as { error?: unknown } | null;
    if (body && typeof body.error === 'string') return body.error;
    if (error.status === 401) return 'Sign in to use the merge queue from here.';
  }
  return 'The queue could not be asked right now; try again in a minute.';
}
