import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { EntityType } from '@mintplayer/ng-spark/models';
import { SparkDetailContext } from '@mintplayer/ng-spark/panels';
import { SparkPoDiffComponent } from '@mintplayer/ng-spark/grid';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkLanguageService, SparkService } from '@mintplayer/ng-spark/services';
import { proposalChanges } from './proposal-diff';
import { SparkApprovalsService } from './spark-approvals.service';
import { SPARK_APPROVAL_REQUEST_TYPE, SparkApprovalDecisionResult, SparkApprovalProposal } from './spark-approvals.models';

/** The custom actions the server disables per row (`ApprovalRequestActions.OnDisableActionsAsync`). */
const APPROVE = 'ApprovalApprove';
const REJECT = 'ApprovalReject';
const WITHDRAW = 'ApprovalWithdraw';

/**
 * The approval card on an `ApprovalRequest`'s detail page (commerce #521, D9/D26), registered by
 * {@link provideSparkApprovals} as a `SPARK_DETAIL_PANELS` entry; renders nothing on any other type.
 *
 * For a document proposal it loads `GET /spark/approvals/proposal` and shows the attribute diff (a
 * creation: every proposed value; a modification: what changes, now → proposed) with a warning when the
 * row moved since. Under it the decision buttons: Approve; Reject, with a required reason; Withdraw for the
 * requester. Which apply is the server's answer on the row (`disabledActions`), so a caller who may not
 * decide sees none; the server checks again on submit. After a decision the page reloads.
 */
@Component({
  selector: 'spark-approval-panel',
  imports: [BsAlertComponent, BsCardComponent, BsCardHeaderComponent, BsFormComponent, BsFormControlDirective, BsSpinnerComponent, SparkPoDiffComponent, TranslateKeyPipe],
  templateUrl: './spark-approval-panel.component.html',
  styleUrl: './spark-approval-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkApprovalPanelComponent {
  private readonly approvals = inject(SparkApprovalsService);
  private readonly spark = inject(SparkService);
  private readonly lang = inject(SparkLanguageService);

  context = input.required<SparkDetailContext>();

  protected readonly colors = Color;

  protected readonly visible = computed(() => this.context().entityType?.name === SPARK_APPROVAL_REQUEST_TYPE);

  protected readonly status = computed(() =>
    String(this.context().item.attributes.find(a => a.name === 'Status')?.value ?? ''));

  private readonly disabled = computed(() => new Set(this.context().item.disabledActions ?? []));
  private readonly pending = computed(() => this.status() === 'Pending');

  protected readonly canApprove = computed(() => this.pending() && !this.disabled().has(APPROVE));
  protected readonly canReject = computed(() => this.pending() && !this.disabled().has(REJECT));
  protected readonly canWithdraw = computed(() => this.pending() && !this.disabled().has(WITHDRAW));

  protected readonly proposal = signal<SparkApprovalProposal | null>(null);
  protected readonly proposalType = signal<EntityType | null>(null);
  protected readonly loading = signal(false);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  /** The reject form: open, and the reason typed so far. */
  protected readonly rejecting = signal(false);
  protected readonly reason = signal('');

  protected readonly changes = computed(() => proposalChanges(this.proposalType(), this.proposal()));
  protected readonly isCreate = computed(() => this.proposal()?.operation === 'Create' || !this.proposal()?.current);

  constructor() {
    // (Re)load the proposal when another request is opened. A reload after a decision keeps the id and
    // re-reads too, since the proposal's status and staleness changed with it.
    effect(() => {
      const ctx = this.context();
      const visible = this.visible();
      untracked(() => {
        if (!visible) return;
        void this.loadProposal(ctx.id);
      });
    });
  }

  protected async approve(): Promise<void> {
    await this.run(() => this.approvals.decide(this.context().id, 'approve'));
  }

  protected openReject(): void {
    this.reason.set('');
    this.rejecting.set(true);
  }

  protected cancelReject(): void {
    this.rejecting.set(false);
  }

  protected async reject(): Promise<void> {
    const reason = this.reason().trim();
    if (!reason) {
      this.error.set(this.lang.t('approvals.reasonRequired'));
      return;
    }
    await this.run(() => this.approvals.decide(this.context().id, 'reject', reason));
  }

  protected async withdraw(): Promise<void> {
    await this.run(() => this.approvals.withdraw(this.context().id));
  }

  protected onReasonInput(event: Event): void {
    this.reason.set((event.target as HTMLTextAreaElement).value);
  }

  private async run(decision: () => Promise<SparkApprovalDecisionResult>): Promise<void> {
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const result = await decision();
      if (result.outcome !== 'done') {
        this.error.set(this.describeOutcome(result.outcome));
        // A conflict means the request moved on (decided elsewhere, or stale): show where it is now.
        if (result.outcome !== 'reasonRequired') await this.context().reload();
        return;
      }
      this.rejecting.set(false);
      await this.context().reload();
    } catch (e) {
      const err = e as HttpErrorResponse;
      this.error.set(err?.status === 404 || err?.status === 401
        ? this.lang.t('approvals.unavailable')
        : err?.error?.error || err?.message || this.lang.t('common.actionFailed'));
    } finally {
      this.busy.set(false);
    }
  }

  private async loadProposal(id: string): Promise<void> {
    this.loading.set(true);
    try {
      const proposal = await this.approvals.proposal(id);
      // A slow answer for a request the page has since left is dropped.
      if (this.context().id !== id) return;
      this.proposal.set(proposal);
      this.proposalType.set(proposal ? await this.spark.getEntityType(proposal.objectTypeId) : null);
    } catch {
      this.proposal.set(null);
      this.proposalType.set(null);
    } finally {
      this.loading.set(false);
    }
  }

  private describeOutcome(outcome: string): string {
    switch (outcome) {
      case 'reasonRequired': return this.lang.t('approvals.reasonRequired');
      case 'notPending': return this.lang.t('approvals.outcomeNotPending');
      case 'stale': return this.lang.t('approvals.outcomeStale');
      case 'unwritable': return this.lang.t('approvals.outcomeUnwritable');
      case 'cancelled': return this.lang.t('approvals.outcomeCancelled');
      default: return this.lang.t('approvals.outcomeInvalid');
    }
  }
}
