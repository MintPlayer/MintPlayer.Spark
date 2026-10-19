import { describe, expect, it } from 'vitest';

import {
  SparkDetailContext,
  SparkDetailTitleBadge,
  sparkSaveSubmittedRoute,
  sparkTitleBadgesFor,
} from './spark-panels';

// Commerce #521 M3: the detail title's badges are a registry (core's "Deleted" is its first entry), and a
// diverted save asks registered handlers where to go.

function context(overrides: Partial<SparkDetailContext> = {}): SparkDetailContext {
  return {
    type: 'doc', id: 'docs/1',
    item: { id: 'docs/1', name: 'Doc', objectTypeId: 't', attributes: [] } as any,
    entityType: { id: 't', name: 'Doc', attributes: [] } as any,
    permissions: null,
    deleted: null,
    reload: async () => undefined,
    ...overrides,
  };
}

const pending: SparkDetailTitleBadge = {
  id: 'pending',
  order: 10,
  badge: ctx => ctx.item.metadata?.['approval'] ? { labelKey: 'approvals.pendingBadge', color: 'warning', link: ['/po', 'approval-request', 'r/1'] } : null,
};

describe('detail title badges', () => {
  it('draws core\'s Deleted badge only on a row opened from the recycle bin', () => {
    expect(sparkTitleBadgesFor([], context())).toEqual([]);
    const badges = sparkTitleBadgesFor(null, context({ deleted: 'only' }));
    expect(badges.map(b => b.id)).toEqual(['spark-deleted']);
    expect(badges[0].labelKey).toBe('softDelete.deleted');
    expect(badges[0].cssClass).toBe('spark-deleted-badge');
  });

  it('adds a registered badge when it applies, after core\'s by order', () => {
    const item = { id: 'docs/1', name: 'Doc', objectTypeId: 't', attributes: [], metadata: { approval: { requestId: 'r/1' } } } as any;
    const badges = sparkTitleBadgesFor([pending], context({ item, deleted: 'only' }));
    expect(badges.map(b => b.id)).toEqual(['spark-deleted', 'pending']);
    expect(badges[1].link).toEqual(['/po', 'approval-request', 'r/1']);
    expect(sparkTitleBadgesFor([pending], context()).map(b => b.id)).toEqual([]);
  });

  it('lets a registration replace core\'s badge by id', () => {
    const replaced: SparkDetailTitleBadge = { id: 'spark-deleted', badge: () => ({ labelKey: 'custom.deleted' }) };
    const badges = sparkTitleBadgesFor([replaced], context());
    expect(badges.map(b => b.labelKey)).toEqual(['custom.deleted']);
  });

  it('draws nothing before the page has a context', () => {
    expect(sparkTitleBadgesFor([pending], null)).toEqual([]);
  });
});

describe('diverted-save handlers', () => {
  const submitted = { submitted: { kind: 'approval', referenceId: 'approvals/1' } };

  it('answers the first non-null route, in registration order', () => {
    const route = sparkSaveSubmittedRoute(
      [() => null, ctx => ['/po', 'approval-request', ctx.submitted.submitted.referenceId], () => ['/never']],
      { operation: 'create', type: 'doc', submitted });
    expect(route).toEqual(['/po', 'approval-request', 'approvals/1']);
  });

  it('answers null without handlers, so the page keeps its own behaviour', () => {
    expect(sparkSaveSubmittedRoute(null, { operation: 'update', type: 'doc', id: 'docs/1', submitted })).toBeNull();
    expect(sparkSaveSubmittedRoute([() => null], { operation: 'update', type: 'doc', id: 'docs/1', submitted })).toBeNull();
  });
});
