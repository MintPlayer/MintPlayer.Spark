import { Provider } from '@angular/core';
import { SparkAttributeRendererRegistration } from '@mintplayer/ng-spark/renderers';
import {
  SparkDetailTitleBadge,
  SparkSaveSubmittedHandler,
  provideSparkDetailPanels,
  provideSparkDetailTitleBadges,
  provideSparkSaveSubmittedHandlers,
} from '@mintplayer/ng-spark/panels';
import { SparkApprovalPanelComponent } from './spark-approval-panel.component';
import { SparkApprovalStatusComponent } from './spark-approval-status.component';
import { SPARK_APPROVAL_REQUEST_ALIAS, approvalMetadataOf } from './spark-approvals.models';

/** The route of an approval request's detail page. */
export function approvalRequestRoute(requestId: string): unknown[] {
  return ['/po', SPARK_APPROVAL_REQUEST_ALIAS, requestId];
}

/**
 * "Approval pending" after the title of a governed row with a pending proposal the caller made or may
 * decide (`metadata.approval`), linking to the request.
 */
export const sparkApprovalPendingBadge: SparkDetailTitleBadge = {
  id: 'spark-approval-pending',
  order: 10,
  badge: context => {
    const approval = approvalMetadataOf(context.item);
    return approval && (approval.status ?? 'Pending') === 'Pending'
      ? { labelKey: 'approvals.pendingBadge', color: 'warning', cssClass: 'spark-approval-pending-badge', link: approvalRequestRoute(approval.requestId) }
      : null;
  },
};

/**
 * After a creation diverted for approval, open the approval request: the object does not exist until it is
 * approved, so the create page's `/po/:type/:id` would be a 404. An edit keeps the default (the detail page,
 * which shows {@link sparkApprovalPendingBadge}).
 */
export const sparkApprovalSubmittedHandler: SparkSaveSubmittedHandler = context =>
  context.operation === 'create' && context.submitted.submitted.kind === 'approval'
    ? approvalRequestRoute(context.submitted.submitted.referenceId)
    : null;

/**
 * Adds the Approvals UI (commerce #521, D9/D26) to the pages `sparkRoutes()` routes to: the decision panel
 * (proposal diff, Approve / Reject / Withdraw) on every approval request, the "Approval pending" title badge
 * on governed rows, and the redirect of a diverted creation to its request. Needs the server package
 * `MintPlayer.Spark.Approvals`.
 *
 * ```ts
 * providers: [
 *   provideSpark(...),
 *   provideSparkApprovals(),
 *   provideSparkAttributeRenderers([...sparkApprovalsRenderers, ...yourRenderers]),
 * ]
 * // shell: <spark-approvals-inbox-link linkClass="btn btn-sm btn-link" />
 * ```
 *
 * The status pill is an attribute renderer (`"renderer": "approval-status"` on `ApprovalRequest.Status`),
 * so it is registered through {@link sparkApprovalsRenderers} with the app's own renderers —
 * `provideSparkAttributeRenderers` takes one list.
 */
export function provideSparkApprovals(options?: { panelOrder?: number }): Provider[] {
  return [
    ...provideSparkDetailPanels({ id: 'spark-approval-panel', component: SparkApprovalPanelComponent, order: options?.panelOrder ?? 10 }),
    ...provideSparkDetailTitleBadges(sparkApprovalPendingBadge),
    ...provideSparkSaveSubmittedHandlers(sparkApprovalSubmittedHandler),
  ];
}

/** The status pill as an attribute renderer named `approval-status` (detail page and grid column). */
export const sparkApprovalsRenderers: SparkAttributeRendererRegistration[] = [
  {
    name: 'approval-status',
    detailComponent: SparkApprovalStatusComponent,
    columnComponent: SparkApprovalStatusComponent,
  },
];
