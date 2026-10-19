import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SPARK_APPROVAL_STATUSES, SparkApprovalStatus } from './spark-approvals.models';

/**
 * The `approval-status` attribute renderer (commerce #521): an approval request's status as a coloured
 * pill, on the detail page and in grid columns. The colour is a class per status
 * (`spark-approval-status-pending`, …) styled in the sibling .scss, so an app restyles it in CSS.
 * An unknown status renders as its raw text, neutral.
 */
@Component({
  selector: 'spark-approval-status',
  imports: [TranslateKeyPipe],
  templateUrl: './spark-approval-status.component.html',
  styleUrl: './spark-approval-status.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkApprovalStatusComponent {
  /** The status (the renderer contract's `value`). */
  value = input<unknown>(null);

  protected readonly status = computed(() => (this.value() == null ? '' : String(this.value())));
  protected readonly known = computed(() => SPARK_APPROVAL_STATUSES.includes(this.status() as SparkApprovalStatus));
  protected readonly cssClass = computed(() =>
    `spark-approval-status spark-approval-status-${this.known() ? this.status().toLowerCase() : 'unknown'}`);
}
