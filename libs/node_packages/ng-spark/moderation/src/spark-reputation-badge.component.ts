import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkModerationService } from './spark-moderation.service';
import { ModerationReputation } from './spark-moderation.models';

/**
 * A user's reputation (#460): `<spark-reputation-badge [userId]="post.authorId" />`, or without
 * `userId` the signed-in user's own, with the not-yet-credited part (delayed crediting) beside it.
 *
 * The own badge usually sits in the app shell, which lives for the whole session: it re-reads the
 * reputation after every navigation (a sign-in, a sign-out, opening another page) and after the
 * caller's own votes, or it would show the first value it read until a full page reload.
 */
@Component({
  selector: 'spark-reputation-badge',
  imports: [BsBadgeComponent, TranslateKeyPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (reputation(); as r) {
      <bs-badge [type]="colors.secondary" class="spark-reputation-badge" [attr.title]="'moderation.reputation' | t">
        {{ r.total }}
        @if (!userId() && r.pending) {
          <!-- A received downvote makes the pending part negative: "(-2 pending)", never "(+-2 pending)". -->
          <span class="fw-normal spark-reputation-pending">({{ r.pending > 0 ? '+' : '' }}{{ r.pending }} {{ 'moderation.pending' | t }})</span>
        }
      </bs-badge>
    }
  `,
})
export class SparkReputationBadgeComponent {
  private readonly moderation = inject(SparkModerationService);

  protected readonly colors = Color;

  userId = input<string | null | undefined>();

  protected readonly reputation = signal<ModerationReputation | null>(null);

  constructor() {
    effect(() => {
      const userId = this.userId() ?? undefined;
      // The own badge only: someone else's total does not move because the viewer navigated.
      const read = userId
        ? this.moderation.reputation(userId)
        : (this.moderation.ownReputationChanged(), this.moderation.ownReputation());
      read.then(r => this.reputation.set(r), () => this.reputation.set(null));
    });
  }
}
