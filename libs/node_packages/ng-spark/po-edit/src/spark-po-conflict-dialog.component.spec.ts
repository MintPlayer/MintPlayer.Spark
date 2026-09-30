import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { SparkLanguageService, SparkService } from '@mintplayer/ng-spark/services';
import { AS_DETAIL_ROW_KEY, EntityAttributeDefinition, EntityType, ShowedOn } from '@mintplayer/ng-spark/models';
import { MergeConflict, mergeThreeWay } from './conflict-merge';
import { SparkPoConflictDialogComponent } from './spark-po-conflict-dialog.component';
import { addReferenceLabelsOf, addReferenceLabelsOfOptions } from './reference-labels';

function attr(name: string, extra: Partial<EntityAttributeDefinition> = {}): EntityAttributeDefinition {
  return { id: name, name, dataType: 'string', isRequired: false, isVisible: true, isReadOnly: false, order: 1, showedOn: ShowedOn.PersistentObject, ...extra } as any;
}

const lineType: EntityType = {
  id: 't-line', name: 'Line', clrType: 'Test.Line',
  attributes: [
    attr('Text', { order: 1 }),
    attr('Driver', { dataType: 'Reference', referenceType: 'Test.Person', order: 2 }),
    attr('Secret', { isVisible: false, order: 3 }),
  ],
} as any;

const car = attr('Owner', { dataType: 'Reference', referenceType: 'Test.Person', query: 'People' });
const lines = attr('Lines', { dataType: 'AsDetail', asDetailType: 'Test.Line', isArray: true, order: 2 });
const schema = { attributes: [car, lines], resolve: (clr: string) => (clr === 'Test.Line' ? lineType : undefined) };

const row = (key: string, values: Record<string, unknown>) => ({ [AS_DETAIL_ROW_KEY]: key, ...values });

describe('SparkPoConflictDialogComponent', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideNoopAnimations(),
        { provide: SparkService, useValue: { getLookupReference: vi.fn() } },
        { provide: SparkLanguageService, useValue: { t: (k: string) => k, resolve: (ts: any) => ts?.en ?? '' } },
      ],
    });
  });

  function open(conflicts: MergeConflict[], referenceLabels: Record<string, string> = {}) {
    const fixture = TestBed.createComponent(SparkPoConflictDialogComponent);
    fixture.componentRef.setInput('conflicts', conflicts);
    fixture.componentRef.setInput('referenceLabels', referenceLabels);
    fixture.detectChanges();
    return { fixture, groups: (fixture.componentInstance as any).groups() as any[] };
  }

  it('renders a reference by its label on both sides, whichever read or pick it came from', () => {
    const { conflicts } = mergeThreeWay(
      { Owner: 'people/1', Lines: [] },
      { Owner: 'people/2', Lines: [] },
      { Owner: 'people/3', Lines: [] },
      schema,
    );
    const { groups } = open(conflicts, { 'people/2': 'Picked Paula', 'people/3': 'Their Tom' });

    const [entry] = groups[0].entries;
    expect(entry.mine.display).toBe('Picked Paula');
    expect(entry.theirs.display).toBe('Their Tom');
  });

  it('falls back to the id for a reference nobody has a label for', () => {
    const { conflicts } = mergeThreeWay({ Owner: 'people/1' }, { Owner: 'people/2' }, { Owner: 'people/3' }, { ...schema, attributes: [car] });
    const { groups } = open(conflicts, {});
    expect(groups[0].entries[0].mine.display).toBe('people/2');
  });

  it('renders a row conflict as the row\'s cells, mine beside theirs, marking what differs', () => {
    const base = { Owner: 'people/1', Lines: [row('l1', { Text: 'a', Driver: 'people/1', Secret: 's' })] };
    const mine = { Owner: 'people/1', Lines: [row('l1', { Text: 'mine', Driver: 'people/2', Secret: 's' })] };
    const theirs = { Owner: 'people/1', Lines: [] };
    const { conflicts } = mergeThreeWay(base, mine, theirs, schema);
    expect(conflicts.map(c => c.kind)).toEqual(['row']);

    const { groups } = open(conflicts, { 'people/2': 'Paula' });
    const [entry] = groups[0].entries;

    // Mine: the row, attribute by attribute (hidden attributes left out), with references labelled.
    expect(entry.mine.text).toBeUndefined();
    expect(entry.mine.fields.map((f: any) => f.attribute.name)).toEqual(['Text', 'Driver']);
    expect(entry.mine.fields.map((f: any) => f.cell.display)).toEqual(['mine', 'Paula']);
    // Theirs removed it: that is the "remove" half of the keep/remove choice.
    expect(entry.theirs.fields).toBeUndefined();
    expect(entry.theirs.text).toBe('common.conflictRowRemoved');
  });

  it('marks the attributes a row differs in when both sides hold it', () => {
    const conflict: MergeConflict = {
      path: 'Lines[l9]', kind: 'row', attribute: lines, rootAttribute: lines, group: '',
      rowAttributes: lineType.attributes, rowTypeName: 'Line',
      base: undefined,
      mine: row('l9', { Text: 'same', Driver: 'people/2' }),
      theirs: row('l9', { Text: 'same', Driver: 'people/3' }),
    };
    const { groups } = open([conflict], { 'people/2': 'Paula', 'people/3': 'Tom' });
    const [entry] = groups[0].entries;
    expect(entry.mine.fields.map((f: any) => f.differs)).toEqual([false, true]);
    expect(entry.theirs.fields.map((f: any) => f.cell.display)).toEqual(['same', 'Tom']);
  });

  it('shows a row as one line only when its type did not resolve', () => {
    const conflict: MergeConflict = {
      path: 'Lines[l9]', kind: 'row', attribute: lines, rootAttribute: lines, group: '',
      base: undefined, mine: row('l9', { Text: 'x' }), theirs: undefined,
    };
    const { groups } = open([conflict]);
    expect(groups[0].entries[0].mine.text).toBe('x');
  });
});

describe('reference labels', () => {
  it('collects labels from a read, rows and reference arrays included, and from picker candidates', () => {
    const labels: Record<string, string> = {};
    addReferenceLabelsOf({
      id: 'cars/1', name: 'Car', objectTypeId: 't',
      attributes: [
        { id: 'o', name: 'Owner', dataType: 'Reference', value: 'people/1', breadcrumb: 'Ann' },
        { id: 'm', name: 'Many', dataType: 'Reference', isArray: true, value: ['people/2'], breadcrumbs: { 'people/2': 'Bob' } },
        { id: 'n', name: 'Name', dataType: 'string', value: 'plain' },
        {
          id: 'l', name: 'Lines', dataType: 'AsDetail', isArray: true,
          objects: [{ id: 'l1', name: 'Line', objectTypeId: 'x', attributes: [{ id: 'd', name: 'Driver', dataType: 'Reference', value: 'people/3', breadcrumb: 'Cy' }] }],
        },
      ],
    } as any, labels);
    addReferenceLabelsOfOptions([[{ id: 'people/4', breadcrumb: 'Dee', values: [] }, { id: 'people/1', breadcrumb: 'not the first', values: [] }]], labels);

    expect(labels).toEqual({ 'people/1': 'Ann', 'people/2': 'Bob', 'people/3': 'Cy', 'people/4': 'Dee' });
  });
});
