import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';
import { SparkReputationBadgeComponent } from '@mintplayer/ng-spark/moderation';

/**
 * The `AuthorId` cell and field (renderer `qna-author`). A post stores its author's **id** only
 * (#460 D8: never a name, so a deleted account needs no rewrite), and an id is not worth reading —
 * so the cell shows the author's reputation badge (Moderation, M12) instead. Signed out the badge stays
 * empty: the reputation endpoint answers an anonymous caller with no result (never a 401, which would
 * send the whole app to the sign-in page).
 */
@Component({
  selector: 'app-author-renderer',
  imports: [SparkIconComponent, SparkReputationBadgeComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (authorId(); as id) {
      <span class="qna-author" [attr.data-author-id]="id">
        <spark-icon name="person" />
        <spark-reputation-badge [userId]="id" />
      </span>
    }
  `,
})
export class AuthorRendererComponent {
  value = input<unknown>();

  protected readonly authorId = computed(() => {
    const value = this.value();
    return typeof value === 'string' && value.length > 0 ? value : null;
  });
}
