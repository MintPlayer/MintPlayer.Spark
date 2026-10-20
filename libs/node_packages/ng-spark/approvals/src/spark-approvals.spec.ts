import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { SparkLanguageService, SparkService } from '@mintplayer/ng-spark/services';
import { EntityType, ShowedOn } from '@mintplayer/ng-spark/models';
import {
  SPARK_DETAIL_PANELS,
  SPARK_DETAIL_TITLE_BADGES,
  SPARK_SAVE_SUBMITTED_HANDLERS,
  SparkDetailContext,
  sparkSaveSubmittedRoute,
  sparkTitleBadgesFor,
} from '@mintplayer/ng-spark/panels';
import { SparkApprovalsService } from './spark-approvals.service';
import { SparkApprovalPanelComponent } from './spark-approval-panel.component';
import { SparkApprovalStatusComponent } from './spark-approval-status.component';
import { proposalChanges } from './proposal-diff';
import { provideSparkApprovals, sparkApprovalsRenderers } from './provide-spark-approvals';
import { SparkApprovalProposal } from './spark-approvals.models';

// Commerce #521 M3 (D9/D26): the ng-spark client of MintPlayer.Spark.Approvals.

const companyType: EntityType = {
  id: 't-company', name: 'Company', alias: 'company', clrType: 'Test.Company',
  attributes: [
    { id: 'a1', name: 'Name', dataType: 'string', order: 1, showedOn: ShowedOn.PersistentObject } as any,
    { id: 'a2', name: 'City', dataType: 'string', order: 2, showedOn: ShowedOn.PersistentObject } as any,
    { id: 'a3', name: 'Secret', dataType: 'string', order: 3, showedOn: ShowedOn.None } as any,
  ],
} as any;

function po(values: Record<string, unknown>, extra: Record<string, unknown> = {}): any {
  return {
    id: 'companies/1', name: 'Company', objectTypeId: 't-company',
    attributes: Object.entries(values).map(([name, value]) => ({ id: name, name, value })),
    ...extra,
  };
}

function requestContext(overrides: { status?: string; disabledActions?: string[] } = {}): SparkDetailContext {
  return {
    type: 'approval-request',
    id: 'ApprovalRequests/1-A',
    item: po({ Title: 'Change to Company Acme', Status: overrides.status ?? 'Pending' }, {
      id: 'ApprovalRequests/1-A', objectTypeId: 't-request', disabledActions: overrides.disabledActions ?? [],
    }),
    entityType: { id: 't-request', name: 'ApprovalRequest', attributes: [] } as any,
    permissions: null,
    deleted: null,
    reload: vi.fn().mockResolvedValue(undefined),
  };
}

const flush = () => new Promise(resolve => setTimeout(resolve, 0));

describe('approvals: the service', () => {
  let spark: { postEnvelope: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    spark = { postEnvelope: vi.fn().mockResolvedValue({ outcome: 'done', status: 'Approved', attributes: [], errors: [] }) };
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: SparkService, useValue: spark }],
    });
  });

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('posts each decision with the id in the body', async () => {
    const service = TestBed.inject(SparkApprovalsService);

    await service.decide('ApprovalRequests/1-A', 'approve');
    await service.decide('ApprovalRequests/1-A', 'reject', 'Wrong city');
    await service.withdraw('ApprovalRequests/1-A');
    await service.revoke('ApprovalRequests/1-A', 'Approved by mistake');

    expect(spark.postEnvelope.mock.calls).toEqual([
      ['/approvals/decide', { id: 'ApprovalRequests/1-A', decision: 'approve' }],
      ['/approvals/decide', { id: 'ApprovalRequests/1-A', decision: 'reject', reason: 'Wrong city' }],
      ['/approvals/withdraw', { id: 'ApprovalRequests/1-A' }],
      ['/approvals/revoke', { id: 'ApprovalRequests/1-A', reason: 'Approved by mistake' }],
    ]);
  });

  it('resolves a refusal by rule (409) to its outcome, and rejects the standard refusal (404)', async () => {
    const service = TestBed.inject(SparkApprovalsService);
    spark.postEnvelope.mockRejectedValueOnce(new HttpErrorResponse({ status: 409, error: { result: { outcome: 'stale', status: 'Stale' } } }));
    await expect(service.decide('r/1', 'approve')).resolves.toMatchObject({ outcome: 'stale', status: 'Stale' });

    spark.postEnvelope.mockRejectedValueOnce(new HttpErrorResponse({ status: 404 }));
    await expect(service.decide('r/1', 'approve')).rejects.toMatchObject({ status: 404 });
  });

  it('reads a proposal with GET ?id=, and answers null for a request that is not one', async () => {
    const service = TestBed.inject(SparkApprovalsService);
    const http = TestBed.inject(HttpTestingController);

    const found = service.proposal('ApprovalRequests/1-A');
    const request = http.expectOne(r => r.url === '/spark/approvals/proposal');
    expect(request.request.method).toBe('GET');
    expect(request.request.params.get('id')).toBe('ApprovalRequests/1-A');
    request.flush({ result: { requestId: 'ApprovalRequests/1-A', operation: 'Create' } });
    expect((await found)?.operation).toBe('Create');

    const missing = service.proposal('ApprovalRequests/2-A');
    http.expectOne(r => r.url === '/spark/approvals/proposal').flush(null, { status: 404, statusText: 'Not Found' });
    expect(await missing).toBeNull();
  });
});

describe('approvals: the proposal diff', () => {
  const base = { requestId: 'r/1', status: 'Pending', objectTypeId: 't-company', isStale: false } as const;

  it('lists every shown proposed value for a creation', () => {
    const proposal: SparkApprovalProposal = { ...base, operation: 'Create', targetId: null, current: null, proposed: po({ Name: 'Acme', Secret: 'x' }) };
    const changes = proposalChanges(companyType, proposal);
    expect(changes.map(c => c.attribute.name)).toEqual(['Name']);
    expect(changes[0].revision).toBeUndefined();
    expect(changes[0].current?.value).toBe('Acme');
  });

  it('lists the changed attributes for a modification, current → proposed, ignoring what the proposal left out', () => {
    const proposal: SparkApprovalProposal = {
      ...base, operation: 'Modify', targetId: 'companies/1',
      current: po({ Name: 'Acme', City: 'Ghent' }),
      proposed: po({ Name: 'Acme NV' }),
    };
    const changes = proposalChanges(companyType, proposal);
    expect(changes.map(c => [c.attribute.name, c.revision?.value, c.current?.value])).toEqual([['Name', 'Acme', 'Acme NV']]);
  });
});

describe('approvals: registration', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideSparkApprovals()] });
  });

  it('registers the panel, the pending badge, the submitted handler and the status renderer', () => {
    expect(TestBed.inject(SPARK_DETAIL_PANELS).map(p => p.component)).toEqual([SparkApprovalPanelComponent]);
    expect(TestBed.inject(SPARK_DETAIL_TITLE_BADGES).map(b => b.id)).toEqual(['spark-approval-pending']);
    expect(TestBed.inject(SPARK_SAVE_SUBMITTED_HANDLERS)).toHaveLength(1);
    expect(sparkApprovalsRenderers).toEqual([
      { name: 'approval-status', detailComponent: SparkApprovalStatusComponent, columnComponent: SparkApprovalStatusComponent },
    ]);
  });

  it('badges a governed row with a pending proposal, linking to the request', () => {
    const context = { ...requestContext(), item: po({}, { metadata: { approval: { requestId: 'ApprovalRequests/1-A', status: 'Pending', own: true, mayDecide: false } } }) };
    const badges = sparkTitleBadgesFor(TestBed.inject(SPARK_DETAIL_TITLE_BADGES), context);
    expect(badges.map(b => [b.id, b.labelKey, b.link])).toEqual([
      ['spark-approval-pending', 'approvals.pendingBadge', ['/po', 'approval-request', 'ApprovalRequests/1-A']],
    ]);
    expect(sparkTitleBadgesFor(TestBed.inject(SPARK_DETAIL_TITLE_BADGES), requestContext())).toEqual([]);
  });

  it('sends a diverted creation to its request and leaves a diverted edit to the default', () => {
    const handlers = TestBed.inject(SPARK_SAVE_SUBMITTED_HANDLERS);
    const submitted = { submitted: { kind: 'approval', referenceId: 'ApprovalRequests/1-A' } };
    expect(sparkSaveSubmittedRoute(handlers, { operation: 'create', type: 'company', submitted }))
      .toEqual(['/po', 'approval-request', 'ApprovalRequests/1-A']);
    expect(sparkSaveSubmittedRoute(handlers, { operation: 'update', type: 'company', id: 'companies/1', submitted })).toBeNull();
    expect(sparkSaveSubmittedRoute(handlers, { operation: 'create', type: 'company', submitted: { submitted: { kind: 'other', referenceId: 'x' } } })).toBeNull();
  });
});

describe('approvals: the decision panel', () => {
  let approvals: { decide: ReturnType<typeof vi.fn>; withdraw: ReturnType<typeof vi.fn>; proposal: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    approvals = {
      decide: vi.fn().mockResolvedValue({ outcome: 'done', status: 'Approved' }),
      withdraw: vi.fn().mockResolvedValue({ outcome: 'done', status: 'Withdrawn' }),
      proposal: vi.fn().mockResolvedValue(null),
    };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: SparkApprovalsService, useValue: approvals },
        { provide: SparkService, useValue: { getEntityType: vi.fn().mockResolvedValue(companyType), getLookupReference: vi.fn() } },
        { provide: SparkLanguageService, useValue: { t: (k: string) => k } },
      ],
    });
  });

  async function render(context: SparkDetailContext) {
    const fixture = TestBed.createComponent(SparkApprovalPanelComponent);
    fixture.componentRef.setInput('context', context);
    fixture.detectChanges();
    await flush();
    fixture.detectChanges();
    return fixture;
  }

  it('renders nothing on another type', async () => {
    const fixture = await render({ ...requestContext(), entityType: { id: 't', name: 'Company' } as any });
    expect(fixture.nativeElement.querySelector('.spark-approval-panel')).toBeNull();
    expect(approvals.proposal).not.toHaveBeenCalled();
  });

  it('approves with the request id, then reloads the page', async () => {
    const context = requestContext();
    const fixture = await render(context);

    fixture.nativeElement.querySelector('.spark-approval-approve').click();
    await flush();

    expect(approvals.proposal).toHaveBeenCalledWith('ApprovalRequests/1-A');
    expect(approvals.decide).toHaveBeenCalledWith('ApprovalRequests/1-A', 'approve');
    expect(context.reload).toHaveBeenCalled();
  });

  it('rejects only with a reason, and sends it trimmed', async () => {
    const fixture = await render(requestContext());
    fixture.nativeElement.querySelector('.spark-approval-reject').click();
    fixture.detectChanges();

    const confirm: HTMLButtonElement = fixture.nativeElement.querySelector('.spark-approval-reject-confirm');
    expect(confirm.disabled).toBe(true);

    const textarea: HTMLTextAreaElement = fixture.nativeElement.querySelector('#spark-approval-reason');
    textarea.value = '  Wrong city  ';
    textarea.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(confirm.disabled).toBe(false);

    confirm.click();
    await flush();
    expect(approvals.decide).toHaveBeenCalledWith('ApprovalRequests/1-A', 'reject', 'Wrong city');
  });

  it('offers only Withdraw to the requester, and posts it', async () => {
    const fixture = await render(requestContext({ disabledActions: ['ApprovalApprove', 'ApprovalReject'] }));
    expect(fixture.nativeElement.querySelector('.spark-approval-approve')).toBeNull();
    expect(fixture.nativeElement.querySelector('.spark-approval-reject')).toBeNull();

    fixture.nativeElement.querySelector('.spark-approval-withdraw').click();
    await flush();
    expect(approvals.withdraw).toHaveBeenCalledWith('ApprovalRequests/1-A');
  });

  it('offers nothing once the request is decided', async () => {
    const fixture = await render(requestContext({ status: 'Approved' }));
    expect(fixture.nativeElement.querySelector('.spark-approval-buttons')).toBeNull();
  });

  it('shows the outcome of a refusal by rule', async () => {
    approvals.decide.mockResolvedValueOnce({ outcome: 'notPending', status: 'Rejected' });
    const fixture = await render(requestContext());
    fixture.nativeElement.querySelector('.spark-approval-approve').click();
    await flush();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.spark-approval-error').textContent).toContain('approvals.outcomeNotPending');
  });

  it('warns about a stale proposal and does not offer Approve', async () => {
    approvals.proposal.mockResolvedValueOnce({
      requestId: 'ApprovalRequests/1-A', status: 'Pending', operation: 'Modify', objectTypeId: 't-company', targetId: 'companies/1',
      isStale: true, current: po({ Name: 'Acme' }), proposed: po({ Name: 'Acme NV' }),
    });
    const fixture = await render(requestContext());
    await flush();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.spark-approval-stale')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('spark-po-diff')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('.spark-approval-approve').disabled).toBe(true);
  });
});
