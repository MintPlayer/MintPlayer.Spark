import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkModerationService } from './spark-moderation.service';

/**
 * A link to the review queue (#460) that renders only for a caller who may review — earned, or
 * granted by a group such as moderators (`canReview` on the caller's own reputation). Anyone else
 * would open a page whose every request answers 404. Re-checked after each navigation, so it appears
 * on sign-in and disappears on sign-out.
 *
 * `<spark-review-queue-link linkClass="btn btn-sm btn-link" />` — `linkClass` styles the anchor.
 */
@Component({
  selector: 'spark-review-queue-link',
  imports: [RouterLink, TranslateKeyPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[style.display]': 'canReview() ? null : "none"' },
  template: `
    @if (canReview()) {
      <a class="spark-review-queue-link" [class]="linkClass()" routerLink="/moderation/review">{{ 'moderation.reviewQueue' | t }}</a>
    }
  `,
})
export class SparkReviewQueueLinkComponent {
  private readonly moderation = inject(SparkModerationService);

  /** Classes for the anchor itself (the host element is only a wrapper). */
  linkClass = input<string>('');

  protected readonly canReview = signal(false);

  constructor() {
    effect(() => {
      this.moderation.ownReputationChanged();
      this.moderation.ownReputation().then(r => this.canReview.set(r.canReview === true), () => this.canReview.set(false));
    });
  }
}
