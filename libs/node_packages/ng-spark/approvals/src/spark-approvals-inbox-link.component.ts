import { ChangeDetectionStrategy, Component, effect, inject, input, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { SPARK_AUTH_STATE } from '@mintplayer/ng-spark';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkLiveService } from '@mintplayer/ng-spark/services';
import {
  SPARK_APPROVALS_INBOX_TOPIC,
  SPARK_PENDING_APPROVALS_QUERY_ALIAS,
  SPARK_PENDING_APPROVALS_QUERY_ID,
} from './spark-approvals.models';

/**
 * A link to the caller's approval inbox (commerce #521): the `pending-approvals` query, with the number
 * of requests awaiting the caller's decision. Renders only while that number is above zero, so a caller
 * who decides nothing never sees it. For the app shell, like `spark-review-queue-link`:
 *
 * `<spark-approvals-inbox-link linkClass="btn btn-sm btn-link" />`
 *
 * The count is the query's `totalItems`, followed live over the socket (no HTTP request, so a signed-out
 * visitor is never sent to the sign-in page). The server's private `approvals:inbox` topic, published to
 * each recipient when a request reaches or leaves their inbox, re-reads it; so does a sign-in or sign-out.
 */
@Component({
  selector: 'spark-approvals-inbox-link',
  imports: [RouterLink, TranslateKeyPipe],
  templateUrl: './spark-approvals-inbox-link.component.html',
  styleUrl: './spark-approvals-inbox-link.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[style.display]': 'count() > 0 ? null : "none"' },
})
export class SparkApprovalsInboxLinkComponent {
  private readonly live = inject(SparkLiveService);
  private readonly authState = inject(SPARK_AUTH_STATE, { optional: true });

  /** Classes for the anchor itself (the host element is only a wrapper). */
  linkClass = input<string>('');

  protected readonly link = ['/query', SPARK_PENDING_APPROVALS_QUERY_ALIAS];
  protected readonly count = signal(0);

  /** Bumped by every inbox frame: a fresh subscription yields a fresh snapshot of the count. */
  private readonly inboxChanged = signal(0);

  constructor() {
    // Followed again on a sign-in or sign-out: a refused (anonymous) stream ends with an error.
    effect(onCleanup => {
      this.authState?.();
      untracked(() => {
        const topic = this.live.watchPrivate(SPARK_APPROVALS_INBOX_TOPIC).subscribe(event => {
          if (event.type === 'topic') this.inboxChanged.update(n => n + 1);
        });
        onCleanup(() => topic.unsubscribe());
      });
    });

    effect(onCleanup => {
      this.authState?.();
      this.inboxChanged();
      untracked(() => {
        const subscription: Subscription = this.live
          .watchQuery({ queryId: SPARK_PENDING_APPROVALS_QUERY_ID, skip: 0, take: 1 })
          .subscribe(event => this.count.set(event.type === 'result' ? event.result.totalItems ?? 0 : 0));
        onCleanup(() => subscription.unsubscribe());
      });
    });
  }
}
