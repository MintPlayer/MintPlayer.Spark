import {
  afterNextRender, ChangeDetectionStrategy, Component, computed, DestroyRef, effect, ElementRef, inject, input, signal,
  untracked,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { BsCardBodyComponent, BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import {
  BsDatatableColumnDirective, BsDatatableComponent, BsRowTemplateDirective, DatatableSettings,
  type BsDatatableFetch, type BsDatatableRowEvent,
} from '@mintplayer/ng-bootstrap/datatable';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsSelectComponent, BsSelectOption } from '@mintplayer/ng-bootstrap/select';
import { SparkTrendOptions, SparkTrendSparklineComponent } from '@mintplayer/ng-spark/score-renderers';
import {
  BrowseService, CommitGraphPage, CommitGraphQuery, CommitGraphRef, CoverageDrop, RepoInfo,
} from '../../services/browse.service';
import { isDarkBackground, LANE_PALETTE } from './commit-graph-palette';
import {
  branchCoverageSeries, derivePage, firstParentPath, GraphRowView, LANE_WIDTH, PathHighlight, pieWedge, ROW_HEIGHT,
} from './commit-graph-rows';
import { formatSparkDateTime } from '@mintplayer/ng-spark/date-time';

/** Rows per request: the endpoint's cap (PRD §4.4). */
export const GRAPH_PAGE_SIZE = 200;
/** Visible lanes before the graph column clips (D16): desktop, then phone width. */
export const LANE_CAP = 12;
export const LANE_CAP_MOBILE = 4;
/** Syncing polls back off from the first to the last interval, doubling. */
export const POLL_FIRST_MS = 2000;
export const POLL_MAX_MS = 30000;
const SEARCH_DEBOUNCE_MS = 300;
const NODE_R = 5;
/** The coverage-drop list's default threshold, percentage points. */
export const DROP_THRESHOLD_DEFAULT = 1;

type PanelState = 'loading' | 'ready' | 'empty' | 'syncing' | 'unavailable';

/** The display values of one row, computed when its page lands. */
interface RowUi {
  strokes: string[];
  stroke: string;
  nodeClass: string;
  pie: string | null;
  title: string;
  refs: { name: string; short: string; tag: boolean }[];
  percent: string | null;
  pending: boolean;
  delta: string | null;
  up: boolean;
  initials: string;
  date: string;
  sha: string;
  /** The row's open button: everything the aria-hidden SVG says, as text. */
  label: string;
}

type PanelRow = GraphRowView & { ui: RowUi };


const UNAVAILABLE_REASONS: Record<string, string> = {
  disabled: 'The commit graph is not enabled on this server.',
  unsupported: 'The commit graph is not available for this forge.',
  'no-access': 'The repository could not be read.',
  'installation-cap': 'Too many repositories are mirrored for this installation.',
  'too-many-refs': 'The repository has too many branches and tags to mirror.',
  'too-large': 'The repository is too large to mirror.',
  'disk-floor': 'The server is low on disk space; try again later.',
  timeout: 'Mirroring the repository took too long.',
  auth: 'The forge refused access to the repository.',
  'not-found': 'The forge does not know this repository.',
  error: 'The graph could not be loaded.',
};

/**
 * The SourceTree-style commit graph of a repository (#504, PRD §4.5): every branch and tag in date order,
 * one small SVG per row in ng-bootstrap's virtual datatable, coverage drawn on every node.
 *
 * - Pages of 200 rows are fetched as the viewer scrolls, all from one server `snapshot`.
 * - The lane geometry is the pure `commit-graph-rows.ts`; this component only wires it up.
 * - Lane colours follow the theme: `--cg-lane-{i}` on the host, re-set when `<html>`'s theme flips.
 */
@Component({
  selector: 'app-repo-commit-graph-panel',
  imports: [
    FormsModule, BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent, BsSelectComponent, BsSelectOption,
    BsFormComponent, BsFormControlDirective, BsDatatableComponent, BsDatatableColumnDirective, BsRowTemplateDirective,
    SparkTrendSparklineComponent,
  ],
  template: `
    <bs-card class="mt-3 d-block">
      <bs-card-header><i class="bi bi-diagram-2"></i> Commit graph</bs-card-header>
      <bs-card-body>
        <div class="cg-toolbar d-flex flex-wrap align-items-center gap-2 mb-2">
          <bs-select class="cg-ref" [size]="'sm'" [ngModel]="selectedRef()" (ngModelChange)="selectRef($event)"
                     aria-label="Branches shown">
            <option [ngValue]="''">All branches and tags</option>
            @for (b of branches(); track b.name) {
              <option [ngValue]="b.name">{{ shortRef(b.name) }}{{ isDefault(b.name) ? ' (default)' : '' }}</option>
            }
          </bs-select>
          <button type="button" class="btn btn-sm" [class.btn-secondary]="firstParent()"
                  [class.btn-outline-secondary]="!firstParent()" [attr.aria-pressed]="firstParent()"
                  (click)="toggleFirstParent()">First parent only</button>
          <bs-select class="cg-search-mode" [size]="'sm'" [ngModel]="searchMode()" (ngModelChange)="setSearchMode($event)"
                     aria-label="Search in">
            <option [ngValue]="'message'">Message or SHA</option>
            <option [ngValue]="'author'">Author</option>
          </bs-select>
          <bs-form class="cg-search">
            <input type="search" class="form-control-sm" placeholder="Search commits" aria-label="Search commits"
                   [ngModel]="searchText()" (ngModelChange)="onSearch($event)" name="cg-search">
          </bs-form>
          <button type="button" class="btn btn-sm cg-drops-toggle" [class.btn-secondary]="dropMode()"
                  [class.btn-outline-secondary]="!dropMode()" [attr.aria-pressed]="dropMode()"
                  (click)="toggleDrops()">Coverage drops</button>
          @if (dropMode()) {
            <bs-form class="cg-threshold">
              <input type="number" class="form-control-sm" min="0" max="100" step="0.1" name="cg-threshold"
                     aria-label="Minimum coverage drop, in percentage points" title="Minimum drop, percentage points"
                     [ngModel]="dropThreshold()" (ngModelChange)="setDropThreshold($event)">
            </bs-form>
          }
          @if (sparkline(); as points) {
            <span class="cg-spark small text-muted ms-auto">
              {{ shortRef(sparkRef() ?? '') }}
              <spark-trend-sparkline [value]="points" [options]="sparklineOptions()" />
            </span>
          }
        </div>

        @if (notice(); as n) {
          <div class="small text-muted mb-2" role="status">{{ n }}</div>
        }

        @if (dropMode()) {
          @if (drops(); as list) {
            @if (list.length) {
              <ul class="cg-drops list-unstyled mb-0" aria-label="Commits that dropped coverage">
                @for (d of list; track d.sha) {
                  <li>
                    <button type="button" class="cg-drop" (click)="showDrop(d)" [attr.aria-label]="dropLabel(d)">
                      <code>{{ d.sha.substring(0, 7) }}</code>
                      <span class="text-danger">▼{{ (-d.deltaVsParent).toFixed(1) }}</span>
                      @if (d.percent !== null) { <span>{{ d.percent.toFixed(1) }}%</span> }
                      <span class="cg-drop-msg">{{ d.message }}</span>
                      <small class="text-muted">{{ d.branch }}{{ d.date ? ' · ' + formatDate(d.date) : '' }}</small>
                    </button>
                  </li>
                }
              </ul>
            } @else {
              <div class="small text-muted cg-empty" role="status">
                No commit dropped coverage by more than {{ dropThreshold() }} points.
              </div>
            }
          } @else if (dropsFailed()) {
            <div class="small text-muted" role="status">{{ unavailableText }}</div>
          } @else {
            <div class="small text-muted" role="status">Loading the commits that dropped coverage…</div>
          }
        } @else {
        @switch (state()) {
          @case ('loading') {
            <div class="small text-muted" role="status">Loading the commit graph…</div>
          }
          @case ('syncing') {
            <div class="small text-muted cg-syncing" role="status">
              Mirroring the repository. The graph appears as soon as the first clone finishes.
            </div>
          }
          @case ('unavailable') {
            <div class="small text-muted cg-unavailable" role="status">{{ reasonText() }}</div>
          }
          @case ('empty') {
            <div class="small text-muted cg-empty" role="status">
              {{ listMode() ? 'No commits match the search.' : 'This repository has no commits yet.' }}
            </div>
          }
          @case ('ready') {
            <div class="cg-table" [class.cg-has-hl]="highlight().size > 0" tabindex="0" role="group"
                 aria-label="Commit graph; use the arrow keys to move and Enter to open a commit"
                 (keydown)="onKeydown($event)" (mouseover)="onHover($event)" (mouseleave)="hover.set(null)">
              <!-- virtualBuffer 3: mp-datatable re-renders and re-checks every row in the window on each range change (S9). -->
              <bs-datatable [fetch]="fetchFn()" [virtualScroll]="true" [itemSize]="rowHeight" [virtualBuffer]="3"
                            [isResponsive]="true" [resizableColumns]="false" [rowKey]="rowKey"
                            [(settings)]="settings" (rowClick)="onRowClick($event)">
                <div *bsDatatableColumn="'graph'; sortable: false; label: 'Graph'"><span class="visually-hidden">Graph</span></div>
                <div *bsDatatableColumn="'subject'; sortable: false">Message</div>
                <div *bsDatatableColumn="'coverage'; sortable: false">Coverage</div>
                <div *bsDatatableColumn="'author'; sortable: false">Author</div>
                <div *bsDatatableColumn="'date'; sortable: false">Date</div>
                <div *bsDatatableColumn="'sha'; sortable: false">Commit</div>

                <ng-container *bsRowTemplate="let item">
                  @let v = $any(item);
                  @if (v) {
                    @let hl = highlight().get(v.index);
                    <td class="cg-graph" [style.width.px]="graphWidth()">
                      <svg [attr.width]="graphWidth()" [attr.height]="rowHeight" aria-hidden="true">
                        @for (s of v.segments; track s.key; let i = $index) {
                          <path [attr.d]="s.d" [attr.stroke]="v.ui.strokes[i]" [class.hl]="hl?.has(s.key)" />
                        }
                        @if (v.isMerge) {
                          <circle class="cg-merge" [attr.cx]="v.cx" [attr.cy]="v.cy" [attr.r]="nodeR + 2.5" [attr.stroke]="v.ui.stroke" />
                        }
                        <circle [class]="v.ui.nodeClass" [class.hl]="hl?.has('n')" [attr.cx]="v.cx" [attr.cy]="v.cy"
                                [attr.r]="nodeR" [attr.stroke]="v.ui.stroke"><title>{{ v.ui.title }}</title></circle>
                        @if (v.ui.pie) {
                          <path class="cg-pie" [attr.d]="v.ui.pie" [attr.fill]="v.ui.stroke" />
                        }
                        @if (v.width > visibleLanes()) {
                          <path class="cg-overflow" [attr.d]="overflowMarker()" />
                        }
                      </svg>
                    </td>
                    <td class="cg-subject" [class.cg-cursor]="cursor() === v.index">
                      @for (r of v.ui.refs; track r.name) {
                        <span class="cg-ref-pill" [class.cg-tag]="r.tag" [class.cg-default]="r.name === defaultRef()">{{ r.short }}</span>
                      }
                      @if (v.row.sha === latestCoverageSha()) {
                        <span class="cg-ref-pill cg-latest" title="Latest coverage of the default branch">coverage</span>
                      }
                      <span [class.fw-semibold]="v.isMerge">{{ v.row.subject }}</span>
                    </td>
                    <td class="cg-coverage">
                      @if (v.ui.percent) {
                        {{ v.ui.percent }}
                        @if (v.ui.delta) {
                          <small [class.text-success]="v.ui.up" [class.text-danger]="!v.ui.up">{{ v.ui.delta }}</small>
                        }
                      } @else if (v.ui.pending) {
                        <span class="text-muted">pending</span>
                      } @else {
                        <span class="visually-hidden">no report</span>
                      }
                    </td>
                    <td class="cg-author">
                      @if (v.row.avatarKey && !failedAvatars().has(v.row.avatarKey)) {
                        <img class="cg-avatar" [src]="avatarSrc(v.row.avatarKey)" alt="" width="20" height="20" loading="lazy"
                             (error)="avatarFailed(v.row.avatarKey)">
                      } @else {
                        <span class="cg-avatar cg-initials" aria-hidden="true">{{ v.ui.initials }}</span>
                      }
                      {{ v.row.authorName }}
                    </td>
                    <td class="cg-date" [title]="v.row.date">{{ v.ui.date }}</td>
                    <td class="cg-sha">
                      <button type="button" class="cg-open" [attr.aria-label]="v.ui.label" (click)="openFromButton($event, v)">
                        <code>{{ v.ui.sha }}</code>
                      </button>
                    </td>
                  } @else {
                    <td class="cg-graph" [style.width.px]="graphWidth()"></td>
                    <td class="text-muted">…</td><td></td><td></td><td></td><td></td>
                  }
                </ng-container>
              </bs-datatable>
            </div>
          }
        }
        }
      </bs-card-body>
    </bs-card>
  `,
  styles: [`
    /* min-width 0: a flex or grid parent must not grow to the table's natural width (no page scroll on phones). */
    :host { display: block; min-width: 0; max-width: 100%; --mp-datatable-virtual-max-height: 560px; }
    .cg-toolbar > * { min-width: 0; max-width: 100%; }
    .cg-toolbar .cg-search { flex: 1 1 12rem; max-width: 20rem; }
    .cg-toolbar .cg-threshold { width: 5.5rem; }
    .cg-spark { display: inline-flex; align-items: center; gap: .4rem; }
    .cg-table { max-width: 100%; }
    .cg-open { border: 0; padding: 0; background: none; color: inherit; line-height: 20px; }
    .cg-open:focus-visible, .cg-drop:focus-visible { outline: 2px solid var(--bs-primary); outline-offset: 1px; }
    .cg-drops li + li { border-top: 1px solid var(--bs-border-color-translucent); }
    .cg-drop { display: flex; gap: .5rem; align-items: baseline; width: 100%; min-width: 0; text-align: start; border: 0;
      background: none; color: inherit; padding: .35rem .25rem; }
    .cg-drop:hover { background: var(--bs-tertiary-bg); }
    .cg-drop-msg { flex: 1 1 auto; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    @media (max-width: 575.98px) {
      .cg-toolbar .cg-search { max-width: none; }
      :host .cg-table td.cg-subject { max-width: 12rem; }
      .cg-drop small { display: none; }
    }
    .cg-table:focus-visible { outline: 2px solid var(--bs-primary); outline-offset: 2px; }
    /* Exactly ROW_HEIGHT, no row border: the virtual scroll's itemSize and the SVG lines both depend on it. */
    .cg-table td { height: ${ROW_HEIGHT}px; max-height: ${ROW_HEIGHT}px; padding: 0 .5rem; box-sizing: border-box; vertical-align: middle; border: 0;
      line-height: 20px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    /* Mixed alignments grow the line box past 20px and the row past ROW_HEIGHT. */
    .cg-table td > * { vertical-align: top; }
    .cg-table td.cg-graph { padding: 0; line-height: 0; }
    .cg-graph svg { display: block; overflow: hidden; }
    .cg-graph path { fill: none; stroke-width: 2; }
    .cg-has-hl .cg-graph path:not(.hl):not(.cg-pie), .cg-has-hl .cg-node:not(.hl) { opacity: .35; }
    .cg-graph path.hl { stroke-width: 3; }
    .cg-node { stroke-width: 2; fill: var(--bs-body-bg); }
    .cg-node.cg-none { fill: none; }
    .cg-node.cg-pending { stroke: var(--bs-secondary-color) !important; stroke-dasharray: 2 2; }
    .cg-node.hl { stroke-width: 3; }
    .cg-merge { fill: none; stroke-width: 1.5; }
    .cg-overflow { fill: var(--bs-secondary-color); stroke: none !important; }
    .cg-cursor { box-shadow: inset 3px 0 var(--bs-primary); }
    .cg-ref-pill { display: inline-block; box-sizing: border-box; height: 20px; font-size: .75em; line-height: 18px; padding: 0 .4em; margin-right: .3em; border-radius: 1em;
      border: 1px solid var(--bs-border-color); background: var(--bs-secondary-bg); vertical-align: top; }
    .cg-ref-pill.cg-default { font-weight: 600; border-color: var(--bs-primary); }
    .cg-ref-pill.cg-tag { border-radius: .2em; font-style: italic; }
    .cg-ref-pill.cg-latest { border-style: dashed; }
    .cg-avatar { width: 20px; height: 20px; border-radius: 50%; vertical-align: top; margin-right: .3em; display: inline-block; }
    .cg-initials { background: var(--bs-secondary-bg); font-size: 9px; line-height: 20px; text-align: center; }
    .cg-sha code { font-size: .8em; }
    /* The datatable sizes columns from their natural width once; cap the message so the rest stays in view. */
    .cg-table td.cg-subject { max-width: 34rem; }
    .cg-table td.cg-author { max-width: 12rem; }
  `],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RepoCommitGraphPanelComponent {
  private readonly browse = inject(BrowseService);
  private readonly router = inject(Router);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly destroyRef = inject(DestroyRef);

  provider = input.required<string>();
  owner = input.required<string>();
  name = input.required<string>();

  protected readonly rowHeight = ROW_HEIGHT;
  protected readonly nodeR = NODE_R;

  readonly state = signal<PanelState>('loading');
  readonly reason = signal<string | null>(null);
  readonly notice = signal<string | null>(null);
  readonly totalRows = signal(0);
  readonly refs = signal<CommitGraphRef[]>([]);
  readonly repo = signal<RepoInfo | null>(null);
  readonly selectedRef = signal('');
  readonly firstParent = signal(false);
  readonly searchMode = signal<'message' | 'author'>('message');
  readonly searchText = signal('');
  /** The debounced search the walk is filtered by. */
  readonly appliedSearch = signal('');
  readonly fetchFn = signal<BsDatatableFetch<GraphRowView> | null>(null);
  readonly hover = signal<number | null>(null);
  readonly cursor = signal<number | null>(null);
  readonly failedAvatars = signal<ReadonlySet<string>>(new Set());
  /** Lanes of the widest row on the first page, capped (D16). */
  readonly pageLanes = signal(1);
  readonly mobile = signal(false);
  /** Bumped whenever rows land in the cache, so derived views recompute. */
  private readonly cacheVersion = signal(0);

  settings = new DatatableSettings({
    perPage: { values: [GRAPH_PAGE_SIZE], selected: GRAPH_PAGE_SIZE },
    page: { values: [1], selected: 1 },
    sortColumns: [],
  });

  readonly visibleLanes = computed(() => Math.min(this.pageLanes(), this.mobile() ? LANE_CAP_MOBILE : LANE_CAP));
  /** Room for the visible lanes plus the overflow marker. */
  readonly graphWidth = computed(() => this.visibleLanes() * LANE_WIDTH + 8);
  readonly branches = computed(() => this.refs().filter((r) => r.kind === 'head'));
  readonly latestCoverageSha = computed(() => this.repo()?.latestCoverageSha ?? null);
  readonly defaultRef = computed(() => (this.repo()?.defaultBranch ? `refs/heads/${this.repo()!.defaultBranch}` : null));
  /** The rows on show are a search's hit list (flat, no lanes), not the graph. */
  readonly listMode = signal(false);
  readonly reasonText = computed(() => UNAVAILABLE_REASONS[this.reason() ?? ''] ?? UNAVAILABLE_REASONS['error']);
  protected readonly unavailableText = UNAVAILABLE_REASONS['error'];

  /** The coverage-drop list (D17) replaces the graph while on; the graph's walk stays as it was. */
  readonly dropMode = signal(false);
  readonly dropThreshold = signal(DROP_THRESHOLD_DEFAULT);
  /** Null while loading. */
  readonly drops = signal<CoverageDrop[] | null>(null);
  readonly dropsFailed = signal(false);
  private dropGeneration = 0;

  /** The branch the sparkline follows: the selected one, else the default branch. */
  readonly sparkRef = computed(() => this.selectedRef() || this.defaultRef());
  /** Coverage along `sparkRef`'s first parents, over the rows loaded so far (D17); null below two points. */
  readonly sparkline = computed<number[] | null>(() => {
    this.cacheVersion();
    const ref = this.sparkRef();
    if (!ref || this.listMode() || this.state() !== 'ready') return null;
    const tip = this.refs().find((r) => r.name === ref)?.sha;
    if (!tip) return null;
    const points = branchCoverageSeries(tip, (i) => this.cache.get(i), (sha) => this.shaIndex.get(sha));
    return points.length >= 2 ? points : null;
  });
  readonly sparklineOptions = computed<Record<string, any>>(() => ({
    display: 'sparkline', yMin: 0, yMax: 100, width: '96px', height: '20px', minPoints: 2,
    label: `Coverage along ${this.shortRef(this.sparkRef() ?? '')}, oldest to newest`,
  } satisfies SparkTrendOptions));

  /** The first-parent path of the hovered row, else of the keyboard cursor. */
  readonly highlight = computed<PathHighlight>(() => {
    this.cacheVersion();
    const start = this.hover() ?? this.cursor();
    if (start === null) return new Map();
    return firstParentPath(start, (i) => this.cache.get(i), (sha) => this.shaIndex.get(sha));
  });

  /** Rows by absolute index, for the current snapshot. */
  private cache = new Map<number, GraphRowView>();
  private shaIndex = new Map<string, number>();
  private snapshot: string | null = null;
  private activeQuery: CommitGraphQuery = {};
  private generation = 0;
  private pollTimer: ReturnType<typeof setTimeout> | null = null;
  private pollDelay = POLL_FIRST_MS;
  private searchTimer: ReturnType<typeof setTimeout> | null = null;
  private pendingScroll: number | null = null;
  /** The commit the next effect-driven restart opens at. */
  private pendingAt: string | null = null;

  protected readonly rowKey = (row: GraphRowView | undefined, index: number) => row?.row.sha ?? `p-${index}`;

  constructor() {
    effect(() => {
      const repo = { provider: this.provider(), owner: this.owner(), name: this.name() };
      untracked(() => {
        this.browse.getRepo(repo.provider, repo.owner, repo.name).then((r) => this.repo.set(r), () => this.repo.set(null));
      });
    });

    // Any change of repository or view restarts the walk from the top.
    effect(() => {
      this.provider(); this.owner(); this.name();
      this.selectedRef(); this.firstParent(); this.appliedSearch(); this.searchMode();
      untracked(() => {
        const at = this.pendingAt ?? undefined;
        this.pendingAt = null;
        void this.start(at);
      });
    });

    // The drop list follows the repository, the threshold and the selected branch while it is on.
    effect(() => {
      if (!this.dropMode()) return;
      const repo = { provider: this.provider(), owner: this.owner(), name: this.name() };
      const threshold = this.dropThreshold();
      const ref = this.selectedRef();
      untracked(() => void this.loadDrops(repo.provider, repo.owner, repo.name, threshold, ref));
    });

    afterNextRender(() => {
      const applyTheme = () => this.applyPalette();
      applyTheme();
      const observer = new MutationObserver(applyTheme);
      observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-bs-theme', 'class'] });
      const dark = window.matchMedia?.('(prefers-color-scheme: dark)');
      dark?.addEventListener?.('change', applyTheme);
      const narrow = window.matchMedia?.('(max-width: 575.98px)');
      const onNarrow = () => this.mobile.set(!!narrow?.matches);
      onNarrow();
      narrow?.addEventListener?.('change', onNarrow);
      this.destroyRef.onDestroy(() => {
        observer.disconnect();
        dark?.removeEventListener?.('change', applyTheme);
        narrow?.removeEventListener?.('change', onNarrow);
      });
    });

    this.destroyRef.onDestroy(() => {
      this.generation++;
      this.clearTimers();
      if (this.searchTimer) clearTimeout(this.searchTimer);
    });
  }

  // ---- loading ----

  /**
   * Starts a walk: the first page decides the state; `ready` installs a fresh fetch, which the datatable
   * pages through as the viewer scrolls. `at` opens the walk at a commit (search hits, stale restarts).
   */
  async start(at?: string): Promise<void> {
    const gen = ++this.generation;
    this.clearTimers();
    this.cache = new Map();
    this.shaIndex = new Map();
    this.snapshot = null;
    this.hover.set(null);
    this.cursor.set(null);
    if (this.state() !== 'ready') this.state.set('loading');

    const query = this.baseQuery();
    const sha = !at && this.searchMode() === 'message' && /^[0-9a-f]{7,40}$/i.test(this.appliedSearch().trim())
      ? this.appliedSearch().trim() : at;
    if (sha) {
      // A SHA search jumps to the commit in the unfiltered walk.
      delete query.grep;
      query.at = sha;
    }

    let page: CommitGraphPage;
    try {
      page = await this.browse.getCommitGraph(this.provider(), this.owner(), this.name(), { ...query, count: GRAPH_PAGE_SIZE });
    } catch {
      if (gen !== this.generation) return;
      this.reason.set('error');
      this.state.set('unavailable');
      return;
    }
    if (gen !== this.generation) return;

    switch (page.state) {
      case 'syncing':
        this.state.set('syncing');
        this.pollTimer = setTimeout(() => void this.start(at), this.pollDelay);
        this.pollDelay = Math.min(this.pollDelay * 2, POLL_MAX_MS);
        return;
      case 'unavailable':
        this.reason.set(page.reason);
        this.state.set('unavailable');
        return;
      case 'not-found':
        if (sha && sha !== at) {
          // Not a SHA after all: search messages for it instead.
          this.notice.set(null);
          return this.startPlain(gen, true);
        }
        this.notice.set('That commit is not in this view; showing the newest commits instead.');
        return this.startPlain(gen, false);
      case 'stale':
        // The repository moved between two requests of this very call; begin again once.
        return at ? this.startPlain(gen, false) : void this.start();
    }
    this.pollDelay = POLL_FIRST_MS;
    this.install(page, query);
    if (sha) {
      const index = this.shaIndex.get(sha) ?? [...this.shaIndex].find(([s]) => s.startsWith(sha.toLowerCase()))?.[1];
      if (index !== undefined) {
        this.pendingScroll = index;
        this.cursor.set(index);
      }
    }
  }

  /** Reload from the top without `at`; `grepFallback` keeps a SHA-looking search as a message search. */
  private async startPlain(gen: number, grepFallback: boolean): Promise<void> {
    if (gen !== this.generation) return;
    const query = this.baseQuery();
    if (!grepFallback) delete query.grep;
    const page = await this.browse.getCommitGraph(this.provider(), this.owner(), this.name(), { ...query, count: GRAPH_PAGE_SIZE })
      .catch(() => null);
    if (gen !== this.generation) return;
    if (!page || page.state !== 'ready') {
      this.reason.set(page?.reason ?? 'error');
      this.state.set('unavailable');
      return;
    }
    this.install(page, query);
  }

  private install(page: CommitGraphPage, query: CommitGraphQuery): void {
    const list = !!(query.grep || query.author);
    this.listMode.set(list);
    this.activeQuery = query;
    this.snapshot = page.snapshotId;
    this.totalRows.set(page.totalRows);
    // The first page of a walk carries every ref: the branch selector's options.
    if (page.from === 0 && !list) this.refs.set(page.refs);
    else if (this.refs().length === 0) this.refs.set(page.refs);
    this.pageLanes.set(Math.max(1, page.laneCount));
    this.store(page);
    if (page.totalRows === 0) {
      this.state.set('empty');
      return;
    }
    this.state.set('ready');
    this.fetchFn.set(this.makeFetch(this.generation));
  }

  private store(page: CommitGraphPage): void {
    for (const view of derivePage(page.top, page.rows, page.from)) {
      // Everything a row shows is computed once here: the datatable re-checks every visible row on each scroll frame.
      (view as PanelRow).ui = this.rowUi(view);
      this.cache.set(view.index, view);
      this.shaIndex.set(view.row.sha, view.index);
    }
    this.cacheVersion.update((v) => v + 1);
  }

  private makeFetch(gen: number): BsDatatableFetch<GraphRowView> {
    return async (req) => {
      const perPage = req.perPage || GRAPH_PAGE_SIZE;
      const from = (req.page - 1) * perPage;
      const total = this.totalRows();
      const end = Math.min(from + perPage, total);
      if (!this.hasRange(from, end)) {
        const page = await this.browse.getCommitGraph(this.provider(), this.owner(), this.name(), {
          ...this.activeQuery, at: undefined, from, count: perPage, snapshot: this.snapshot ?? undefined,
        });
        if (gen !== this.generation) throw new Error('superseded');
        if (page.state === 'stale') {
          this.restartAtTop();
          throw new Error('stale snapshot');
        }
        if (page.state !== 'ready') throw new Error(`graph ${page.state}`);
        this.store(page);
      }
      const data: GraphRowView[] = [];
      for (let i = from; i < end; i++) {
        const view = this.cache.get(i);
        if (view) data.push(view);
      }
      if (req.page === 1) queueMicrotask(() => this.flushScroll());
      return { data, totalRecords: total, page: req.page, perPage, totalPages: Math.ceil(total / perPage) };
    };
  }

  private hasRange(from: number, end: number): boolean {
    for (let i = from; i < end; i++) if (!this.cache.has(i)) return false;
    return true;
  }

  /** `stale`: the repository moved under the snapshot. Reopen at the first visible commit, best effort. */
  private restartAtTop(): void {
    const scroller = this.scroller();
    const top = scroller ? Math.floor(scroller.scrollTop / ROW_HEIGHT) : 0;
    const sha = this.cache.get(top)?.row.sha;
    queueMicrotask(() => void this.start(sha));
  }

  private baseQuery(): CommitGraphQuery {
    const search = this.appliedSearch().trim();
    return {
      ref: this.selectedRef() || undefined,
      firstParent: this.firstParent() || undefined,
      grep: search && this.searchMode() === 'message' ? search : undefined,
      author: search && this.searchMode() === 'author' ? search : undefined,
    };
  }

  private clearTimers(): void {
    if (this.pollTimer) clearTimeout(this.pollTimer);
    this.pollTimer = null;
  }

  // ---- interaction ----

  selectRef(ref: string): void {
    this.notice.set(null);
    this.selectedRef.set(ref ?? '');
  }

  toggleFirstParent(): void {
    this.notice.set(null);
    this.firstParent.update((v) => !v);
  }

  setSearchMode(mode: 'message' | 'author'): void {
    this.searchMode.set(mode);
  }

  onSearch(text: string): void {
    this.searchText.set(text ?? '');
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => {
      this.searchTimer = null;
      this.notice.set(null);
      this.appliedSearch.set(this.searchText());
    }, SEARCH_DEBOUNCE_MS);
  }

  onRowClick(event: BsDatatableRowEvent<GraphRowView>): void {
    if (event.row) this.open(event.row);
  }

  /** The row's own button: the same as a row click, without the datatable's row click firing too. */
  openFromButton(event: Event, view: GraphRowView): void {
    event.stopPropagation();
    this.open(view);
  }

  /** A graph row opens its commit; a search hit first shows the commit in the graph (D17). */
  open(view: GraphRowView): void {
    if (this.listMode()) {
      this.showInGraph(view.row.sha);
      return;
    }
    // The commit page exists only for commits the server has a record of; a null overlay means it has none, and the
    // commit route would bounce to the home page.
    if (!view.row.coverage) {
      this.notice.set(`No coverage was uploaded for ${view.row.sha.substring(0, 7)}, so it has no commit page.`);
      return;
    }
    this.notice.set(null);
    void this.router.navigate(['/', this.provider(), 'r', this.owner(), this.name(), 'c', view.row.sha]);
  }

  /** Opens the graph at `sha`, leaving a search list or the drop list. */
  private showInGraph(sha: string): void {
    this.dropMode.set(false);
    // Clearing the search restarts the walk through the effect, which picks up `pendingAt`.
    this.pendingAt = sha;
    this.searchText.set('');
    if (this.appliedSearch()) this.appliedSearch.set('');
    else {
      this.pendingAt = null;
      void this.start(sha);
    }
  }

  toggleDrops(): void {
    this.notice.set(null);
    this.dropMode.update((v) => !v);
  }

  setDropThreshold(value: number | string | null): void {
    const n = Number(value);
    // An empty or half-typed field keeps the last threshold rather than listing every drop.
    if (value === null || value === '' || !Number.isFinite(n)) return;
    this.dropThreshold.set(Math.min(100, Math.max(0, n)));
  }

  /** A drop opens in the graph (`at=`), as a search hit does. */
  showDrop(drop: CoverageDrop): void {
    this.notice.set(null);
    this.showInGraph(drop.sha);
  }

  dropLabel(d: CoverageDrop): string {
    const percent = d.percent !== null ? `, now ${d.percent.toFixed(1)}%` : '';
    return `Show ${d.sha.substring(0, 7)} in the graph: ${d.message ?? 'no message'}; coverage fell ${(-d.deltaVsParent).toFixed(1)} points${percent}`;
  }

  private async loadDrops(provider: string, owner: string, name: string, threshold: number, ref: string): Promise<void> {
    const gen = ++this.dropGeneration;
    this.drops.set(null);
    this.dropsFailed.set(false);
    // Commit.Branch is a bare branch name; a tag selection lists the whole repository.
    const branch = ref.startsWith('refs/heads/') ? ref.substring('refs/heads/'.length) : undefined;
    try {
      const list = await this.browse.getCoverageDrops(provider, owner, name, threshold, branch);
      if (gen === this.dropGeneration) this.drops.set(list);
    } catch {
      if (gen === this.dropGeneration) this.dropsFailed.set(true);
    }
  }

  onKeydown(event: KeyboardEvent): void {
    const total = this.totalRows();
    if (!total) return;
    const current = this.cursor() ?? this.hover();
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      const next = current === null ? 0 : Math.min(total - 1, Math.max(0, current + (event.key === 'ArrowDown' ? 1 : -1)));
      this.cursor.set(next);
      this.hover.set(null);
      this.scrollIntoView(next);
    } else if (event.key === 'Enter' && current !== null && !(event.target as Element | null)?.closest?.('button')) {
      // A focused row button handles its own Enter.
      const view = this.cache.get(current);
      if (view) {
        event.preventDefault();
        this.open(view);
      }
    }
  }

  onHover(event: MouseEvent): void {
    const tr = (event.target as Element | null)?.closest?.('tr[data-row-key]') as HTMLElement | null;
    const index = tr ? this.shaIndex.get(tr.dataset['rowKey'] ?? '') : undefined;
    if (index !== undefined && index !== this.hover()) this.hover.set(index);
  }

  avatarFailed(key: string): void {
    this.failedAvatars.update((s) => new Set(s).add(key));
  }

  // ---- rendering helpers ----

  laneVar(colour: number): string {
    return `var(--cg-lane-${colour})`;
  }

  nodeClass(v: GraphRowView): string {
    const c = v.row.coverage;
    if (!c) return 'cg-node cg-none';
    if (c.percent == null || c.completeness === 'Partial') return 'cg-node cg-pending';
    return 'cg-node';
  }

  /** The node's fill: a wedge as large as the coverage, so it reads without hue. */
  pie(v: GraphRowView): string | null {
    const c = v.row.coverage;
    if (!c || c.percent == null || c.completeness === 'Partial' || c.percent <= 0) return null;
    if (c.percent >= 100) return `M${v.cx - NODE_R} ${v.cy}a${NODE_R} ${NODE_R} 0 1 0 ${NODE_R * 2} 0a${NODE_R} ${NODE_R} 0 1 0 ${-NODE_R * 2} 0`;
    return pieWedge(v.cx, v.cy, NODE_R - 1, c.percent / 100);
  }

  nodeTitle(v: GraphRowView): string {
    const c = v.row.coverage;
    const coverage = !c ? 'no coverage report'
      : c.percent == null ? 'coverage pending'
        : `${c.percent.toFixed(1)}% covered${c.completeness === 'Partial' ? ' (partial)' : ''}`;
    return `${v.row.sha.substring(0, 7)}${v.isMerge ? ' (merge)' : ''}: ${coverage}`;
  }

  overflowMarker(): string {
    const x = this.visibleLanes() * LANE_WIDTH + 2;
    const y = ROW_HEIGHT / 2;
    return `M${x} ${y - 4}L${x + 5} ${y}L${x} ${y + 4}Z`;
  }

  avatarSrc(key: string): string {
    return this.browse.avatarUrl(key);
  }

  initials(name: string): string {
    const parts = (name ?? '').trim().split(/\s+/).filter(Boolean);
    return ((parts[0]?.[0] ?? '') + (parts.length > 1 ? parts[parts.length - 1][0] : '')).toUpperCase() || '?';
  }

  shortRef(ref: string): string {
    return ref.replace(/^refs\/(heads|tags)\//, '');
  }

  isTag(ref: string): boolean {
    return ref.startsWith('refs/tags/');
  }

  isDefault(ref: string): boolean {
    const branch = this.repo()?.defaultBranch;
    return !!branch && ref === `refs/heads/${branch}`;
  }

  formatDate(iso: string): string {
    const d = new Date(iso);
    return isNaN(d.getTime()) ? iso : formatSparkDateTime(d, 'viewer', 'date');
  }

  private rowUi(v: GraphRowView): RowUi {
    const c = v.row.coverage;
    const delta = c?.percent != null && c.deltaVsParent ? c.deltaVsParent : null;
    return {
      strokes: v.segments.map((s) => this.laneVar(s.colour)),
      stroke: this.laneVar(v.colour),
      nodeClass: this.nodeClass(v),
      pie: this.pie(v),
      title: this.nodeTitle(v),
      refs: v.row.refs.map((name) => ({ name, short: this.shortRef(name), tag: this.isTag(name) })),
      percent: c?.percent != null ? `${c.percent.toFixed(1)}%` : null,
      pending: !!c && c.percent == null,
      delta: delta === null ? null : `${delta > 0 ? '▲' : '▼'}${Math.abs(delta).toFixed(1)}`,
      up: delta !== null && delta > 0,
      initials: this.initials(v.row.authorName),
      date: this.formatDate(v.row.date),
      sha: v.row.sha.substring(0, 7),
      label: `${this.listMode() ? 'Show in the graph' : 'Open'} ${this.nodeTitle(v)}${delta === null ? '' : `, ${delta > 0 ? 'up' : 'down'} ${Math.abs(delta).toFixed(1)} points`}; ${v.row.subject}`,
    };
  }


  // ---- DOM ----

  /** Sets `--cg-lane-{i}` on the host from the palette shade that suits the painted background. */
  applyPalette(): void {
    const bg = getComputedStyle(document.documentElement).getPropertyValue('--bs-body-bg').trim() || '#ffffff';
    const dark = isDarkBackground(bg);
    const style = this.host.nativeElement.style;
    LANE_PALETTE.forEach((shade, i) => style.setProperty(`--cg-lane-${i}`, dark ? shade.dark : shade.light));
  }

  private scroller(): HTMLElement | null {
    return this.host.nativeElement.querySelector('.datatable-scroll');
  }

  private scrollIntoView(index: number): void {
    const el = this.scroller();
    if (!el) return;
    const header = el.querySelector('thead')?.getBoundingClientRect().height ?? 0;
    const top = index * ROW_HEIGHT;
    if (top < el.scrollTop) el.scrollTop = top;
    else if (top + ROW_HEIGHT > el.scrollTop + el.clientHeight - header) el.scrollTop = top + ROW_HEIGHT - el.clientHeight + header;
  }

  private flushScroll(): void {
    const index = this.pendingScroll;
    if (index === null) return;
    // The table body exists one render after the first page; try a few frames.
    let tries = 0;
    const attempt = () => {
      const el = this.scroller();
      if (el && el.scrollHeight > index * ROW_HEIGHT) {
        this.pendingScroll = null;
        el.scrollTop = Math.max(0, index * ROW_HEIGHT - el.clientHeight / 3);
      } else if (tries++ < 20) {
        requestAnimationFrame(attempt);
      }
    };
    requestAnimationFrame(attempt);
  }
}
