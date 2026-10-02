import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { SparkQuery } from '@mintplayer/ng-spark/models';
import { SparkLanguageService, SparkService } from '@mintplayer/ng-spark/services';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkDetailContext, SparkQueryRowAction } from '@mintplayer/ng-spark/panels';

/** The source of every generated `{Target}{Property}Contributions` query (`ContributionDescriptor.ContributionsQueryMethod`). */
export const SPARK_CONTRIBUTIONS_QUERY_SOURCE = 'Custom.SparkContributionsOfTarget';

/** `POST /spark/po/revert-contribution` (the Contributions server package). */
export const SPARK_REVERT_CONTRIBUTION_PATH = '/po/revert-contribution';

/** Whether `query` is a generated contributions query (its rows are contributions). */
export function isContributionsQuery(query: Pick<SparkQuery, 'source'> | null | undefined): boolean {
  return query?.source === SPARK_CONTRIBUTIONS_QUERY_SOURCE;
}

/**
 * Makes a contribution current again: every newer visible version of its slot is hidden (reason
 * `reverted`), atomically, by the server. The response's notice ("…newer version(s) were hidden") is
 * dispatched like every operation. Rejects with the server's error (400 "restore it first" for a
 * hidden one, 404 without the right).
 */
export async function revertContribution(spark: SparkService, objectTypeId: string, id: string): Promise<void> {
  await spark.postEnvelope(SPARK_REVERT_CONTRIBUTION_PATH, { objectTypeId, id });
}

/**
 * "Revert to this version" in the row menu of a contributions query — for holders of
 * `RevertContribution/{type}`, outside the recycle bin. Asks first; refreshes the list afterwards.
 */
export const sparkRevertContributionRowAction: SparkQueryRowAction = {
  id: 'spark-revert-contribution',
  labelKey: 'contributions.revert',
  priority: 20,
  isOffered: scope => scope.permissions?.canRevertContribution === true
    && isContributionsQuery(scope.query)
    && scope.deleted !== 'only',
  run: async context => {
    const spark = inject(SparkService);
    const lang = inject(SparkLanguageService);
    if (!confirm(lang.t('contributions.confirmRevert'))) return;
    await revertContribution(spark, context.entityType.id, context.row.id);
    context.reload();
  },
};

/**
 * "Revert to this version" on a contribution's detail page (a `SPARK_DETAIL_ACTIONS` entry): shown for
 * `RevertContribution/{type}` holders on a generated contribution type (one that has the generated
 * contributions query), outside the recycle bin. Reloads the page afterwards.
 */
@Component({
  selector: 'spark-revert-contribution',
  imports: [TranslateKeyPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (offered()) {
      <span class="d-inline-flex gap-2 align-items-center">
        <button type="button" class="btn btn-outline-primary spark-revert-contribution" [disabled]="busy()" (click)="revert()">
          {{ 'contributions.revert' | t }}
        </button>
        @if (error(); as message) {
          <span class="text-danger small spark-revert-contribution-error" role="alert">{{ message }}</span>
        }
      </span>
    }
  `,
})
export class SparkRevertContributionComponent {
  private readonly spark = inject(SparkService);
  private readonly lang = inject(SparkLanguageService);

  context = input.required<SparkDetailContext>();

  /** Whether the type is a generated contribution type: it has the generated contributions query. */
  private readonly isContributionType = signal(false);

  protected readonly offered = computed(() => {
    const ctx = this.context();
    return this.isContributionType() && ctx.permissions?.canRevertContribution === true && ctx.deleted !== 'only';
  });

  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  constructor() {
    effect(() => {
      const ctx = this.context();
      // Asked only of a caller who holds the right — everyone else never sees the button anyway.
      if (ctx.permissions?.canRevertContribution !== true || !ctx.entityType?.name) {
        untracked(() => this.isContributionType.set(false));
        return;
      }
      const name = ctx.entityType.name;
      untracked(() => void this.spark.getQueryByName(`${name}s`)
        .then(query => this.isContributionType.set(isContributionsQuery(query) && query?.entityType === name))
        .catch(() => this.isContributionType.set(false)));
    });
  }

  async revert(): Promise<void> {
    if (this.busy()) return;
    if (!confirm(this.lang.t('contributions.confirmRevert'))) return;
    const ctx = this.context();
    this.busy.set(true);
    this.error.set(null);
    try {
      await revertContribution(this.spark, ctx.entityType.id, ctx.id);
      await ctx.reload();
    } catch (e) {
      const err = e as HttpErrorResponse;
      const first = err?.error?.result?.errors?.[0];
      const validation = typeof first?.errorMessage === 'string' ? first.errorMessage
        : first?.errorMessage ? this.lang.resolve(first.errorMessage) : '';
      this.error.set(validation || err?.error?.result?.error || err?.error?.error || this.lang.t('common.actionFailed'));
    } finally {
      this.busy.set(false);
    }
  }
}
