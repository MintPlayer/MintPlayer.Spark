import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SparkDetailContext } from '@mintplayer/ng-spark/panels';
import { SparkModerationService } from './spark-moderation.service';
import { ModerationStatus } from './spark-moderation.models';
import { describeModerationError } from './spark-vote.component';
import { SPARK_MODERATION_REVIEW_PATH } from './spark-moderation.tokens';

/**
 * The moderator panel under a moderatable object's detail page (#460): lock state and Lock/Unlock
 * (`Lock/T`), open flags with a link to the review queue (`Review/Moderation`), and Suspend author
 * (`Suspend/Moderation`). Restore/Purge and Revert are the SoftDelete and History entry points'.
 * Renders nothing for a caller with none of these rights, or for a type that is not moderatable.
 */
@Component({
  selector: 'spark-moderator-panel',
  imports: [BsCardComponent, BsCardHeaderComponent, TranslateKeyPipe, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (status(); as s) {
      @if (s.canLock || s.canReview || s.canSuspend) {
        <bs-card class="d-block spark-moderator-panel">
          <bs-card-header>{{ 'moderation.panel' | t }}</bs-card-header>
          <div class="p-3 d-flex flex-wrap gap-2 align-items-center">
            @if (s.locked) {
              <span class="badge bg-secondary spark-moderation-locked">{{ 'moderation.locked' | t }}</span>
              @if (s.lockReason) { <span class="small text-muted">{{ s.lockReason }}</span> }
            }
            @if (s.canLock) {
              @if (s.locked) {
                <button type="button" class="btn btn-sm btn-outline-secondary spark-unlock" [disabled]="busy()" (click)="unlock()">{{ 'moderation.unlock' | t }}</button>
              } @else {
                <button type="button" class="btn btn-sm btn-outline-secondary spark-lock" [disabled]="busy()" (click)="lock()">{{ 'moderation.lock' | t }}</button>
              }
            }
            @if (s.canReview && s.openFlags > 0) {
              <a class="btn btn-sm btn-outline-warning spark-open-flags" [routerLink]="reviewPath" [queryParams]="{ case: s.openCaseId }">
                {{ 'moderation.openFlags' | t }}: {{ s.openFlags }}
              </a>
            }
            @if (s.canSuspend && s.authorId) {
              <button type="button" class="btn btn-sm btn-outline-danger spark-suspend-author" [disabled]="busy()" (click)="suspendAuthor(s.authorId)">{{ 'moderation.suspendAuthor' | t }}</button>
            }
            @if (error(); as message) {
              <span class="small text-danger" role="alert">{{ message }}</span>
            }
          </div>
        </bs-card>
      }
    }
  `,
})
export class SparkModeratorPanelComponent {
  private readonly moderation = inject(SparkModerationService);
  private readonly lang = inject(SparkLanguageService);
  protected readonly reviewPath = inject(SPARK_MODERATION_REVIEW_PATH);

  context = input.required<SparkDetailContext>();

  protected readonly status = signal<ModerationStatus | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  constructor() {
    effect(() => {
      const ctx = this.context();
      if (ctx.deleted === 'only') {
        this.status.set(null);
        return;
      }
      // A refusal (not moderatable, not visible) simply hides the panel.
      this.moderation.status(ctx.type, ctx.id).then(s => this.status.set(s), () => this.status.set(null));
    });
  }

  async lock(): Promise<void> {
    const reason = prompt(this.lang.t('moderation.lockReason')) ?? undefined;
    await this.run(() => this.moderation.lock(this.context().type, this.context().id, reason));
  }

  async unlock(): Promise<void> {
    await this.run(() => this.moderation.unlock(this.context().type, this.context().id));
  }

  async suspendAuthor(userId: string): Promise<void> {
    const answer = prompt(this.lang.t('moderation.suspendDays'));
    if (answer === null) return;
    const days = answer.trim() === '' ? null : Number(answer);
    if (days !== null && !(days > 0)) return;
    await this.run(() => this.moderation.suspend(userId, days));
  }

  private async run(work: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await work();
      const ctx = this.context();
      this.status.set(await this.moderation.status(ctx.type, ctx.id));
      await ctx.reload();
    } catch (e) {
      this.error.set(describeModerationError(e));
    } finally {
      this.busy.set(false);
    }
  }
}
