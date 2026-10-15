import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { By } from '@angular/platform-browser';
import { describe, expect, it, vi } from 'vitest';

import { SparkPoFormComponent } from './spark-po-form.component';
import { SparkPrimitiveListEditorComponent } from './spark-primitive-list-editor.component';
import { SparkService, SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SPARK_ATTRIBUTE_RENDERERS } from '@mintplayer/ng-spark/renderers';
import { EntityAttributeDefinition, EntityType, ShowedOn } from '@mintplayer/ng-spark/models';

/**
 * A list of plain values (`"dataType": "string", "isArray": true`, a C# `List<string>`) is edited one
 * input per item through `spark-primitive-list-editor`. It used to fall through to the single
 * textbox, which showed the items comma-joined and posted one string the server refused.
 */

function attr(partial: Partial<EntityAttributeDefinition>): EntityAttributeDefinition {
  return {
    id: partial.name || 'a',
    name: 'a',
    dataType: 'string',
    isRequired: false,
    isReadOnly: false,
    order: 1,
    showedOn: ShowedOn.PersistentObject,
    rules: [],
    ...partial,
  } as EntityAttributeDefinition;
}

const accountType: EntityType = {
  id: 't-account',
  name: 'Account',
  clrType: 'Test.Account',
  attributes: [
    attr({ id: 'a-name', name: 'Name', order: 1 }),
    attr({ id: 'a-excl', name: 'MergeQueueCheckExclusions', isArray: true, order: 2 }),
    attr({ id: 'a-sorted', name: 'Priorities', dataType: 'number', isArray: true, isSortable: true, order: 3 }),
    attr({ id: 'a-tags', name: 'Tags', dataType: 'Reference', isArray: true, query: 'TagQuery', referenceType: 'Test.Tag', order: 4 }),
  ],
} as EntityType;

async function create(formData: Record<string, unknown>) {
  const service: any = {
    executeQueryByName: vi.fn().mockResolvedValue({ columns: [], items: [], totalItems: 0 }),
    getEntityTypes: vi.fn().mockResolvedValue([accountType]),
    getPermissions: vi.fn().mockResolvedValue({ canQuery: true, canRead: true, canCreate: true, canEdit: true, canDelete: true }),
    getLookupReference: vi.fn(),
  };
  TestBed.configureTestingModule({
    providers: [
      provideNoopAnimations(),
      { provide: SparkService, useValue: service },
      { provide: SparkLanguageService, useValue: { t: (k: string) => k } },
      { provide: SPARK_ATTRIBUTE_RENDERERS, useValue: [] },
    ],
  });
  const fixture = TestBed.createComponent(SparkPoFormComponent);
  fixture.componentRef.setInput('entityType', accountType);
  fixture.componentInstance.formData.set(formData);
  fixture.detectChanges();
  for (let i = 0; i < 5; i++) await new Promise<void>(r => setTimeout(r, 0));
  fixture.detectChanges();
  return { fixture, component: fixture.componentInstance };
}

const editors = (fixture: any) =>
  fixture.debugElement.queryAll(By.directive(SparkPrimitiveListEditorComponent));

describe('SparkPoFormComponent — lists of plain values', () => {
  it('isPrimitiveList holds for a string or number list, not for a scalar or a reference array', async () => {
    const { component } = await create({});
    const [name, excl, sorted, tags] = accountType.attributes;

    expect(component.isPrimitiveList(name)).toBe(false);
    expect(component.isPrimitiveList(excl)).toBe(true);
    expect(component.isPrimitiveList(sorted)).toBe(true);
    expect(component.isPrimitiveList(tags)).toBe(false);
  });

  it('renders the list editor for each plain-value list, fed the form value', async () => {
    const { fixture } = await create({ Name: 'acme', MergeQueueCheckExclusions: ['ci/build', 'lint'], Priorities: [1, 2], Tags: [] });

    const found = editors(fixture);
    expect(found).toHaveLength(2);
    const exclusions = found[0].componentInstance as SparkPrimitiveListEditorComponent;
    expect(exclusions.items()).toEqual(['ci/build', 'lint']);
    expect(exclusions.dataType()).toBe('string');
    expect(exclusions.inputId()).toBe('MergeQueueCheckExclusions');
  });

  it('passes isSortable through, so only a [Sortable] list can be reordered', async () => {
    const { fixture } = await create({ MergeQueueCheckExclusions: ['a'], Priorities: [1] });

    const [exclusions, priorities] = editors(fixture).map((d: any) => d.componentInstance as SparkPrimitiveListEditorComponent);
    expect(exclusions.canReorder()).toBe(false);
    expect(priorities.canReorder()).toBe(true);
  });

  it('writes the editor\'s new list into formData', async () => {
    const { fixture, component } = await create({ MergeQueueCheckExclusions: ['a'] });

    const exclusions = editors(fixture)[0].componentInstance as SparkPrimitiveListEditorComponent;
    exclusions.add();

    expect(component.formData()['MergeQueueCheckExclusions']).toEqual(['a', '']);
  });

  it('does not render an attribute the rights made read-only (the attribute-level Edit deny)', async () => {
    const readOnlyType = {
      ...accountType,
      attributes: accountType.attributes.map(a => a.name === 'MergeQueueCheckExclusions' ? { ...a, isReadOnly: true } : a),
    } as EntityType;
    const { fixture } = await create({ MergeQueueCheckExclusions: ['a'] });
    fixture.componentRef.setInput('entityType', readOnlyType);
    fixture.detectChanges();

    const names = editors(fixture).map((d: any) => d.componentInstance.inputId());
    expect(names).toEqual(['Priorities']);
  });
});
