import { ChangeDetectionStrategy, Component, effect, inject, input, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { SPARK_AUTH_STATE } from '@mintplayer/ng-spark';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkLiveService } from '@mintplayer/ng-spark/services';
import {
  SPARK_MY_PENDING_CHANGES_QUERY_ALIAS,
  SPARK_MY_PENDING_CHANGES_QUERY_ID,
  SPARK_REPLICATION_OUTCOMES_TOPIC,
  SparkReplicationOutcome,
} from './spark-replication.models';

/**
 * A link to the caller's "My pending changes" (warehouse §4.2.5): their edits of replicas that wait at the owning
 * module, or ended without being applied (a conflict, a refusal, or the owner unreachable for the whole retry
 * budget). Renders only while there are any. For the app shell:
 *
 * `<spark-pending-changes-link linkClass="btn btn-sm btn-link" />`
 *
 * The count is the `my-pending-changes` query's `totalItems`, followed live over the socket, so an outcome that
 * arrived while the caller was away shows at the next sign-in. A terminal outcome pushed on the private
 * `replication:outcomes` topic re-reads it and turns the link into a warning until the caller opens the list.
 */
@Component({
  selector: 'spark-pending-changes-link',
  imports: [RouterLink, TranslateKeyPipe],
  templateUrl: './spark-pending-changes-link.component.html',
  styleUrl: './spark-pending-changes-link.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[style.display]': 'count() > 0 ? null : "none"' },
})
export class SparkPendingChangesLinkComponent {
  private readonly live = inject(SparkLiveService);
  private readonly authState = inject(SPARK_AUTH_STATE, { optional: true });

  /** Classes for the anchor itself (the host element is only a wrapper). */
  linkClass = input<string>('');

  protected readonly link = ['/query', SPARK_MY_PENDING_CHANGES_QUERY_ALIAS];
  protected readonly count = signal(0);
  /** The last terminal outcome pushed while the page was open; cleared when the list is opened. */
  protected readonly lastOutcome = signal<SparkReplicationOutcome | null>(null);

  private readonly outcomesChanged = signal(0);

  constructor() {
    // Followed again on a sign-in or sign-out: a refused (anonymous) stream ends with an error.
    effect(onCleanup => {
      this.authState?.();
      untracked(() => {
        const topic = this.live.watchPrivate<SparkReplicationOutcome>(SPARK_REPLICATION_OUTCOMES_TOPIC).subscribe(event => {
          if (event.type !== 'topic') return;
          this.lastOutcome.set(event.data);
          this.outcomesChanged.update(n => n + 1);
        });
        onCleanup(() => topic.unsubscribe());
      });
    });

    effect(onCleanup => {
      this.authState?.();
      this.outcomesChanged();
      untracked(() => {
        const subscription: Subscription = this.live
          .watchQuery({ queryId: SPARK_MY_PENDING_CHANGES_QUERY_ID, skip: 0, take: 1 })
          .subscribe(event => this.count.set(event.type === 'result' ? event.result.totalItems ?? 0 : 0));
        onCleanup(() => subscription.unsubscribe());
      });
    });
  }

  protected opened(): void {
    this.lastOutcome.set(null);
  }
}
