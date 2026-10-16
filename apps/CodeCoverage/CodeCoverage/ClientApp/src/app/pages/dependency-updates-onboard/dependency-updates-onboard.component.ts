import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsCardBodyComponent, BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsCheckboxComponent } from '@mintplayer/ng-bootstrap/checkbox';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { describeSparkError, SparkLanguageService, SparkQueryActionsService, SparkService } from '@mintplayer/ng-spark/services';
import { repoLink } from '../../components/dependency-updates/dependency-health';
import { BrowseService, DependencyOnboardingResponse, DependencyOnboardingResult, DependencyOnboardingRow } from '../../services/browse.service';

/** The Spark query behind this page (App_Data/Model/Repository.json, `Custom.Repository_DependencyOnboarding`). */
export const ONBOARDING_QUERY = 'dependency-onboarding';
/** Its query action (App_Data/actions.json, `selectionRule ">0"`). */
export const ENABLE_ACTION = 'EnableDependencyUpdates';

/**
 * The dependency updater's onboarding page (/dependency-updates/onboard, dependency-updates PRD §13.13 R20–R26, R36, R39).
 *
 * The GitHub App's Setup URL lands here after an install or an update, with GitHub's own query string. The page passes that
 * query to `GET api/dependency-updates/onboarding` **untouched**: which parameter narrows to which installation is the forge
 * adapter's business on the server (R32), and the id is never trusted there (R20). Without a query (from the Guides page,
 * the Account page or a Repository page) it lists every repository the viewer manages.
 *
 * **Why the list is the backend's DTO and not `<spark-query-grid>`:** the grid's `customActionExecuted` output carries the
 * action but drops what it returned, and this page has to show `DependencyOnboardingResult` (enabled, and not enabled with
 * the reason, R22). The status columns (syncing, onboarding state and pull request, `coverage.yml`'s merge queue) and the
 * rows that are still syncing (no Repository document yet, so no Spark row) are only in the DTO too. So the page renders the
 * DTO and runs the very same Spark query action through ng-spark (`SparkService.executeCustomAction` with the query's id
 * and the selected document ids): the server re-materializes those ids through the query and re-checks each one (R21),
 * the retry prompt (with or without the merge queue, D-O8) opens in the app's `<spark-retry-action-modal>`, and the
 * action's own notifications reach the toast container. Whether the button shows at all is the server's answer for this
 * caller (`SparkQueryActionsService.actionsFor`), never a client-side role check.
 *
 * Signed-in only, through `sparkAuthGuard` on the route, which keeps the query through the sign-in (R36: the API's 401
 * bounce would lose it, the auth interceptor strips the query).
 */
@Component({
  selector: 'app-dependency-updates-onboard',
  imports: [
    RouterLink, BsAlertComponent, BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent, BsCheckboxComponent, BsTableComponent,
    TranslateKeyPipe,
  ],
  templateUrl: './dependency-updates-onboard.component.html',
  styleUrl: './dependency-updates-onboard.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export default class DependencyUpdatesOnboardComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly browse = inject(BrowseService);
  private readonly spark = inject(SparkService);
  private readonly queryActions = inject(SparkQueryActionsService);
  private readonly lang = inject(SparkLanguageService);

  protected readonly infoColor = Color.info;
  protected readonly warningColor = Color.warning;
  protected readonly dangerColor = Color.danger;
  protected readonly successColor = Color.success;

  readonly response = signal<DependencyOnboardingResponse | null>(null);
  readonly loading = signal(true);
  readonly loadError = signal<string | null>(null);
  /** The Spark action is offered to this caller (the server filters `/spark/actions/list` by security.json). */
  readonly canEnable = signal(false);
  /** Selected Repository document ids. */
  readonly selected = signal<ReadonlySet<string>>(new Set());
  readonly busy = signal(false);
  readonly actionError = signal<string | null>(null);
  readonly result = signal<DependencyOnboardingResult | null>(null);

  readonly rows = computed(() => this.response()?.repositories ?? []);
  /** Rows that can be selected: in the app already (a document id) and not on yet. */
  readonly selectable = computed(() => this.rows().filter(r => this.isSelectable(r)));
  readonly allSelected = computed(() => {
    const ids = this.selectable().map(r => r.repositoryId!);
    return ids.length > 0 && ids.every(id => this.selected().has(id));
  });
  readonly selectedCount = computed(() => this.selected().size);

  /** The setup redirect's query, exactly as it arrived, for the API; `{}` without one. */
  private query: Record<string, string> = {};

  constructor() {
    // Subscribed, not read once: "all my accounts →" is the same route without the query, so the component is reused.
    this.route.queryParamMap.pipe(takeUntilDestroyed()).subscribe(map => {
      this.query = toQuery(map);
      this.result.set(null);
      this.selected.set(new Set());
      void this.load();
    });
    void this.loadAction();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    try {
      this.response.set(await this.browse.getDependencyOnboarding(this.query));
      this.loadError.set(null);
    } catch (e) {
      this.response.set(null);
      this.loadError.set(describeSparkError(e, this.lang, this.lang.t('dependencyOnboardingUi.loadFailed')));
    } finally {
      this.loading.set(false);
    }
    // Drop a selection the new list no longer offers (enabled meanwhile, or gone).
    const keep = new Set(this.selectable().map(r => r.repositoryId!));
    this.selected.set(new Set([...this.selected()].filter(id => keep.has(id))));
  }

  private async loadAction(): Promise<void> {
    try {
      this.canEnable.set((await this.queryActions.actionsFor(ONBOARDING_QUERY)).some(a => a.name === ENABLE_ACTION));
    } catch {
      this.canEnable.set(false);
    }
  }

  isSelectable(row: DependencyOnboardingRow): boolean {
    return !!row.repositoryId && !row.syncing && !row.enabled;
  }

  isSelected(row: DependencyOnboardingRow): boolean {
    return !!row.repositoryId && this.selected().has(row.repositoryId);
  }

  toggle(row: DependencyOnboardingRow, on: boolean): void {
    if (!this.isSelectable(row)) return;
    const next = new Set(this.selected());
    if (on) next.add(row.repositoryId!); else next.delete(row.repositoryId!);
    this.selected.set(next);
  }

  toggleAll(on: boolean): void {
    this.selected.set(on ? new Set(this.selectable().map(r => r.repositoryId!)) : new Set());
  }

  /** Runs `EnableDependencyUpdates` on the selection; the server asks about the merge queue (retry prompt) and re-checks every id. */
  async enable(): Promise<void> {
    const ids = [...this.selected()];
    if (ids.length === 0 || this.busy()) return;
    this.busy.set(true);
    this.actionError.set(null);
    try {
      const [query, entityTypes] = await Promise.all([this.spark.getQuery(ONBOARDING_QUERY), this.spark.getEntityTypes()]);
      const entityType = entityTypes.find(t => t.name === query?.entityType) ?? entityTypes.find(t => t.id === query?.entityType);
      if (!query || !entityType) throw new Error(this.lang.t('dependencyOnboardingUi.actionFailed'));
      const result = await this.spark.executeCustomAction<DependencyOnboardingResult>(entityType.id, ENABLE_ACTION, undefined, ids, undefined, query.id);
      // Undefined: the prompt was cancelled (the action returned before setting a result), so nothing changed.
      if (result) {
        this.result.set(result);
        this.selected.set(new Set());
        await this.load();
      }
    } catch (e) {
      this.actionError.set(describeSparkError(e, this.lang, this.lang.t('dependencyOnboardingUi.actionFailed')));
    } finally {
      this.busy.set(false);
    }
  }

  link(row: DependencyOnboardingRow): string[] {
    return repoLink(row.provider, row.fullName);
  }

  /** The translated word for an onboarding state, falling back to the server's own spelling. */
  onboardingLabel(row: DependencyOnboardingRow): string {
    if (row.syncing) return this.t('dependencyOnboardingUi.state.syncing');
    if (!row.enabled && row.onboarding === 'None') return this.t('dependencyOnboardingUi.state.off');
    return this.t(`dependencyOnboardingUi.state.${row.onboarding}`, row.onboarding);
  }

  /** `coverage.yml`'s merge queue as last read (R26). */
  mergeQueueLabel(row: DependencyOnboardingRow): string {
    return row.mergeQueueInFile
      ? this.t(`dependencyOnboardingUi.mergeQueueInFile.${row.mergeQueueInFile}`, row.mergeQueueInFile)
      : this.t('dependencyOnboardingUi.mergeQueueInFile.unknown');
  }

  /** Why the viewer may manage the row, as the server says it (R39); an unknown value is shown as sent. */
  managedByLabel(row: DependencyOnboardingRow): string {
    return this.t(`dependencyOnboardingUi.managedBy.${row.managedBy}`, row.managedBy);
  }

  t(key: string, fallback?: string): string {
    return this.lang.t(key, fallback);
  }

  format(key: string, ...args: (string | number)[]): string {
    return args.reduce<string>((text, arg, i) => text.split(`{${i}}`).join(String(arg)), this.t(key));
  }
}

/** The query string as one value per key (the first of a repeated key), unchanged otherwise. */
function toQuery(map: { keys: string[]; get(key: string): string | null }): Record<string, string> {
  const query: Record<string, string> = {};
  for (const key of map.keys) {
    const value = map.get(key);
    if (value !== null) query[key] = value;
  }
  return query;
}
