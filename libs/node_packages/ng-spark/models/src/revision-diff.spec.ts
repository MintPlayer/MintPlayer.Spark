import { describe, expect, it } from 'vitest';

import { comparableValue } from './comparable-value';
import type { EntityType } from './entity-type';
import type { PersistentObject } from './persistent-object';
import { diffRevision, revisionAttributes } from './revision-diff';
import { ShowedOn } from './showed-on';

// Moved from History (commerce #521, M3): Approvals' proposal diff uses the same function.

const entityType: EntityType = {
  id: 't-doc', name: 'Doc', alias: 'doc', clrType: 'Test.Doc',
  attributes: [
    { id: 'a2', name: 'Body', dataType: 'string', order: 2, showedOn: ShowedOn.PersistentObject } as any,
    { id: 'a1', name: 'Title', dataType: 'string', order: 1, showedOn: ShowedOn.PersistentObject } as any,
    { id: 'a3', name: 'Hidden', dataType: 'string', order: 3, showedOn: ShowedOn.None } as any,
  ],
} as any;

function po(values: Record<string, unknown>): PersistentObject {
  return {
    id: 'docs/1', name: 'Doc', objectTypeId: 't-doc',
    attributes: Object.entries(values).map(([name, value]) => ({ id: name, name, value } as any)),
  } as any;
}

describe('revision diff', () => {
  it('reports only shown attributes whose content differs', () => {
    const changes = diffRevision(entityType, po({ Title: 'old', Body: 'same', Hidden: 'x' }), po({ Title: 'new', Body: 'same', Hidden: 'y' }));
    expect(changes.map(c => c.attribute.name)).toEqual(['Title']);
    expect(changes[0].revision?.value).toBe('old');
    expect(changes[0].current?.value).toBe('new');
  });

  it('reports an attribute missing on one side as a change', () => {
    const changes = diffRevision(entityType, po({ Title: 'same' }), po({ Title: 'same', Body: 'added' }));
    expect(changes.map(c => c.attribute.name)).toEqual(['Body']);
    expect(changes[0].revision).toBeUndefined();
  });

  it('is empty when either side is missing', () => {
    expect(diffRevision(entityType, null, po({ Title: 'x' }))).toEqual([]);
    expect(diffRevision(entityType, po({ Title: 'x' }), undefined)).toEqual([]);
  });

  it('compares on the shown attributes in display order', () => {
    expect(revisionAttributes(entityType).map(a => a.name)).toEqual(['Title', 'Body']);
    expect(revisionAttributes(null)).toEqual([]);
  });

  it('compares nested rows by their values, not their envelopes', () => {
    const row = (value: string, etag: string) => ({ id: 'r', etag, breadcrumb: etag, attributes: [{ name: 'Qty', value }] });
    const a = { name: 'Lines', objects: [row('1', 'e1')] } as any;
    const b = { name: 'Lines', objects: [row('1', 'e2')] } as any;
    const c = { name: 'Lines', objects: [row('2', 'e1')] } as any;
    expect(comparableValue(a)).toBe(comparableValue(b));
    expect(comparableValue(a)).not.toBe(comparableValue(c));
    expect(comparableValue(undefined)).not.toBe(comparableValue({ name: 'x', value: null } as any));
  });
});
