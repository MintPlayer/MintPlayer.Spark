import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, ParamMap, RouterLink } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { SparkQueryCardComponent } from '@mintplayer/ng-spark/grid';
import { QueryResultItem, valueFor } from '@mintplayer/ng-spark/models';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { describeSparkError, SparkLanguageService } from '@mintplayer/ng-spark/services';
import { repoLink } from '../../components/dependency-updates/dependency-health';
import { BrowseService, DependencyOnboardingSummary } from '../../services/browse.service';

/** The Spark query behind this page (App_Data/Model/DependencyOnboardingRow.json, `Custom.DependencyOnboardingRows`). */
export const ONBOARDING_QUERY = 'dependency-onboarding';
/** Its virtual parent, whose id is the setup redirect's query string (App_Data/Model/DependencyOnboardingScope.json). */
export const ONBOARDING_SCOPE_TYPE = 'DependencyOnboardingScope';

/**
 * The dependency updater's onboarding page (/dependency-updates/onboard, dependency-updates PRD §13.13 R20–R26, R36, R39, D-O14).
 *
 * **The list is Spark's.** `<spark-query-card>` over the query `dependency-onboarding` (virtual type `DependencyOnboardingRow`)
 * brings the search, the sort, the paging, the selection that survives paging, and the query action `EnableDependencyUpdates`
 * (actions.json, `selectionRule ">0"`, `refreshOnCompleted`) in its header, offered only when the server grants it. The
 * action asks about the merge queue through the retry prompt (D-O8, the app's `<spark-retry-action-modal>`), reports through
 * the server's notifications (the toast container), and the grid then reloads with the new state. Nothing of that is here.
 *
 * **Narrowing.** The GitHub App's Setup URL lands here with GitHub's own query string. The page hands that string, untouched,
 * to the grid as the id of its parent `DependencyOnboardingScope` and to the header endpoint; which parameter narrows to which
 * installation is the forge adapter's business on the server (R32), which only ever narrows the viewer's own list (R20, D-O13).
 * Without a query (from the Guides, Account or Repository pages) the grid has no parent and lists everything the viewer manages.
 *
 * **The header** (`GET api/dependency-updates/onboarding`) says what the grid cannot: not linked, sign-in expired, GitHub
 * unreachable, waiting for an owner's approval, narrowed, nothing you manage, and how many repositories are still syncing
 * (no Repository document yet, so no row to select).
 *
 * Signed-in only, through `sparkAuthGuard` on the route, which keeps the query through the sign-in (R36).
 */
@Component({
  selector: 'app-dependency-updates-onboard',
  imports: [RouterLink, BsAlertComponent, SparkQueryCardComponent, TranslateKeyPipe],
  templateUrl: './dependency-updates-onboard.component.html',
  styleUrl: './dependency-updates-onboard.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export default class DependencyUpdatesOnboardComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly browse = inject(BrowseService);
  private readonly lang = inject(SparkLanguageService);

  protected readonly infoColor = Color.info;
  protected readonly warningColor = Color.warning;
  protected readonly dangerColor = Color.danger;

  protected readonly queryId = ONBOARDING_QUERY;

  readonly summary = signal<DependencyOnboardingSummary | null>(null);
  readonly loadError = signal<string | null>(null);

  /** The setup redirect's query string as it arrived (repeated keys kept), `''` without one. */
  readonly scope = signal('');
  /** The grid's parent type: only with a scope, so "all my accounts" sends no parent at all. */
  readonly scopeType = computed(() => (this.scope() ? ONBOARDING_SCOPE_TYPE : ''));

  /**
   * The grid shows unless the header says there is nothing to list (not linked, or nothing managed here). Before the header
   * answers, and when it fails, the grid stands on its own: it is the same listing.
   */
  readonly showGrid = computed(() => {
    const s = this.summary();
    return !s || (s.linked && s.repositories > 0);
  });

  /** A row links to its repository's page; the row id is the Repository document id, `Repositories/{provider}/{id}`. */
  readonly rowRoute = (row: QueryResultItem): unknown[] | null => {
    const provider = /^Repositories\/([a-z]+)\//.exec(row.id ?? '')?.[1];
    const fullName = valueFor(row, 'FullName')?.value;
    return provider && typeof fullName === 'string' && fullName.includes('/') ? repoLink(provider, fullName) : null;
  };

  constructor() {
    // Subscribed, not read once: "all my accounts →" is the same route without the query, so the component is reused.
    this.route.queryParamMap.pipe(takeUntilDestroyed()).subscribe(map => {
      this.scope.set(toScope(map));
      void this.load();
    });
  }

  async load(): Promise<void> {
    try {
      this.summary.set(await this.browse.getDependencyOnboarding(this.scope()));
      this.loadError.set(null);
    } catch (e) {
      this.summary.set(null);
      this.loadError.set(describeSparkError(e, this.lang, this.lang.t('dependencyOnboardingUi.loadFailed')));
    }
  }

  /** After the action: the grid reloads itself (refreshOnCompleted); the header's counts may have moved too. */
  onActionExecuted(): void {
    void this.load();
  }

  format(key: string, ...args: (string | number)[]): string {
    return args.reduce<string>((text, arg, i) => text.split(`{${i}}`).join(String(arg)), this.lang.t(key));
  }
}

/** The query string with every value of every key, repeated keys kept (the server comma-joins them, D-O13). */
export function toScope(map: ParamMap): string {
  const params = new URLSearchParams();
  for (const key of map.keys)
    for (const value of map.getAll(key)) params.append(key, value);
  return params.toString();
}
