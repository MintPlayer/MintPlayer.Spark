import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { ActivatedRoute, Router } from '@angular/router';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';
import { SPARK_RETURN_URL_STATE_KEY, SparkLanguageService, SparkReturnNavigationService } from '@mintplayer/ng-spark/services';
import { SparkDetailContext } from '@mintplayer/ng-spark/panels';
import { SparkSoftDeleteService } from './spark-soft-delete.service';

/**
 * Restore and Purge on the detail page of a row opened from the recycle bin (#460).
 *
 * Shown only when the page loaded the row with `?deleted=only` (every row in the recycle bin is
 * deleted) and the caller holds `Restore/T` / `Purge/T`. The server re-checks both — the row must be
 * deleted and the row gate must pass — so a refusal here is the 404/403 it answers, surfaced inline.
 *
 * Registered by {@link provideSparkSoftDelete} as a `SPARK_DETAIL_ACTIONS` entry.
 */
@Component({
  selector: 'spark-soft-delete-actions',
  imports: [TranslateKeyPipe, SparkIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (deletedView()) {
      <span class="d-inline-flex gap-2 align-items-center">
        @if (canRestore()) {
          <button type="button" class="btn btn-success spark-restore" [disabled]="busy()" (click)="restore()">
            {{ 'softDelete.restore' | t }}
          </button>
        }
        @if (canPurge()) {
          <button type="button" class="btn btn-danger spark-purge" [disabled]="busy()" (click)="purge()">
            <spark-icon name="trash" /> {{ 'softDelete.purge' | t }}
          </button>
        }
        @if (error(); as message) {
          <span class="text-danger small spark-soft-delete-error" role="alert">{{ message }}</span>
        }
      </span>
    }
  `,
})
export class SparkSoftDeleteActionsComponent {
  private readonly softDelete = inject(SparkSoftDeleteService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly returnNavigation = inject(SparkReturnNavigationService);
  private readonly lang = inject(SparkLanguageService);

  context = input.required<SparkDetailContext>();

  protected readonly deletedView = computed(() => this.context().deleted === 'only');
  protected readonly canRestore = computed(() => this.context().permissions?.canRestore === true);
  protected readonly canPurge = computed(() => this.context().permissions?.canPurge === true);

  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  async restore(): Promise<void> {
    if (this.busy()) return;
    const ctx = this.context();
    await this.run(async () => {
      await this.softDelete.restore(ctx.type, ctx.id);
      // Same page, live mode: dropping `?deleted` makes the detail page re-read the row normally.
      // The list this row was opened from travels on: a later Delete still returns there.
      const returnUrl = this.returnNavigation.recordedReturnUrl();
      await this.router.navigate([], {
        relativeTo: this.route,
        queryParams: { deleted: null },
        queryParamsHandling: 'merge',
        replaceUrl: true,
        ...(returnUrl ? { state: { [SPARK_RETURN_URL_STATE_KEY]: returnUrl } } : {}),
      });
    });
  }

  async purge(): Promise<void> {
    if (this.busy()) return;
    if (!confirm(this.lang.t('softDelete.confirmPurge'))) return;
    const ctx = this.context();
    await this.run(async () => {
      await this.softDelete.purge(ctx.type, ctx.id, ctx.item.etag ?? '');
      // The row no longer exists: back to the recycle bin it was opened from — the recorded list, else
      // the type's list in `?deleted=only`. Never "the previous page", which may be anything.
      await this.returnNavigation.returnToList(ctx.entityType?.name, { deletedView: true });
    });
  }

  private async run(work: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await work();
    } catch (e) {
      const err = e as HttpErrorResponse;
      this.error.set(err?.status === 404
        ? this.lang.t('softDelete.unavailable')
        : (err?.error?.error || err?.message || this.lang.t('common.actionFailed')));
    } finally {
      this.busy.set(false);
    }
  }
}
