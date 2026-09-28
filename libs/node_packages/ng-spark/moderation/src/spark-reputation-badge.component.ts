import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkModerationService } from './spark-moderation.service';
import { ModerationReputation } from './spark-moderation.models';

/**
 * A user's reputation (#460): `<spark-reputation-badge [userId]="post.authorId" />`, or without
 * `userId` the signed-in user's own, with the not-yet-credited part (delayed crediting) beside it.
 */
@Component({
  selector: 'spark-reputation-badge',
  imports: [TranslateKeyPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (reputation(); as r) {
      <span class="badge bg-light text-dark border spark-reputation-badge" [attr.title]="'moderation.reputation' | t">
        {{ r.total }}
        @if (!userId() && r.pending) {
          <span class="text-muted spark-reputation-pending">(+{{ r.pending }} {{ 'moderation.pending' | t }})</span>
        }
      </span>
    }
  `,
})
export class SparkReputationBadgeComponent {
  private readonly moderation = inject(SparkModerationService);

  userId = input<string | null | undefined>();

  protected readonly reputation = signal<ModerationReputation | null>(null);

  constructor() {
    effect(() => {
      const userId = this.userId() ?? undefined;
      this.moderation.reputation(userId).then(r => this.reputation.set(r), () => this.reputation.set(null));
    });
  }
}
