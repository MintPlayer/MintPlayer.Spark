import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SparkDetailContext } from '@mintplayer/ng-spark/panels';
import { SparkModerationService } from './spark-moderation.service';
import { describeModerationError } from './spark-vote.component';

/**
 * **Flag** on the detail page of every object (#460). Registered by {@link provideSparkModeration}
 * as a `SPARK_DETAIL_ACTIONS` entry. The button is offered to every signed-in viewer of a live row;
 * the server decides (`Flag/T`, a visible moderatable row) and answers a refusal inline — the
 * permissions endpoint does not report moderation rights, and a type that is not moderatable answers
 * like a missing row, so the button cannot know in advance.
 */
@Component({
  selector: 'spark-flag-button',
  imports: [TranslateKeyPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (context().deleted !== 'only') {
      <span class="d-inline-flex gap-2 align-items-center">
        <button type="button" class="btn btn-outline-warning spark-flag" [disabled]="busy() || done()" (click)="flag()">
          {{ 'moderation.flag' | t }}
        </button>
        @if (done()) {
          <span class="small text-success spark-flag-done">{{ 'moderation.flagged' | t }}</span>
        }
        @if (error(); as message) {
          <span class="small text-danger spark-flag-error" role="alert">{{ message }}</span>
        }
      </span>
    }
  `,
})
export class SparkFlagButtonComponent {
  private readonly moderation = inject(SparkModerationService);
  private readonly lang = inject(SparkLanguageService);

  context = input.required<SparkDetailContext>();

  protected readonly busy = signal(false);
  protected readonly done = signal(false);
  protected readonly error = signal<string | null>(null);

  async flag(): Promise<void> {
    const reason = prompt(this.lang.t('moderation.flagReason'));
    if (!reason?.trim()) return;
    const ctx = this.context();
    this.busy.set(true);
    this.error.set(null);
    try {
      await this.moderation.flag(ctx.type, ctx.id, reason.trim());
      this.done.set(true);
    } catch (e) {
      this.error.set(describeModerationError(e));
    } finally {
      this.busy.set(false);
    }
  }
}
