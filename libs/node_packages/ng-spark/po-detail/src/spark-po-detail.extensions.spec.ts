import { Component, effect, input } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter, Routes } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it, vi } from 'vitest';

import { SparkPoDetailComponent } from './spark-po-detail.component';
import { SparkService, SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SPARK_ATTRIBUTE_RENDERERS } from '@mintplayer/ng-spark/renderers';
import { EntityType, PersistentObject, ShowedOn } from '@mintplayer/ng-spark/models';
import { SparkDetailContext, provideSparkDetailActions, provideSparkDetailPanels } from '@mintplayer/ng-spark/panels';
import { SparkQueryCardComponent } from '@mintplayer/ng-spark/grid';
import { settle, StubComponent } from '../../src/test-utils';

const personType: EntityType = {
  id: 't-person', name: 'Person', alias: 'person', clrType: 'Test.Person',
  attributes: [
    { id: 'a-first', name: 'FirstName', dataType: 'string', isRequired: false, isVisible: true, isReadOnly: false, order: 1, showedOn: ShowedOn.PersistentObject } as any,
  ],
} as any;

const item: PersistentObject = {
  id: 'people/1', name: 'Alice', objectTypeId: 't-person',
  attributes: [{ id: 'a-first', name: 'FirstName', value: 'Alice' } as any],
} as any;

/** Records every context it is handed, so a test can assert what the page passed. */
const seen: SparkDetailContext[] = [];

@Component({ selector: 'test-panel', standalone: true, template: '<div class="test-panel">panel {{ context().item.name }}</div>' })
class TestPanel {
  context = input.required<SparkDetailContext>();
  constructor() { effect(() => { seen.push(this.context()); }); }
}

@Component({ selector: 'test-action', standalone: true, template: '<button class="test-action">act</button>' })
class TestAction {
  context = input.required<SparkDetailContext>();
}

const routes: Routes = [
  { path: 'po/:type/:id', component: SparkPoDetailComponent },
  { path: '', component: StubComponent },
];

async function setup(permissions: Record<string, boolean> = {}) {
  seen.length = 0;
  const service: any = {
    getEntityTypes: vi.fn().mockResolvedValue([personType]),
    get: vi.fn().mockResolvedValue(item),
    getPermissions: vi.fn().mockResolvedValue({ canQuery: true, canRead: true, canCreate: true, canEdit: true, canDelete: true, ...permissions }),
    getCustomActions: vi.fn().mockResolvedValue([
      { name: 'Archive', label: { en: 'Archive' }, showedOn: 'detail', offset: 0 },
      { name: 'Edit', label: { en: 'Edit' }, showedOn: 'both', selectionRule: '=1', offset: 0, isDefault: true },
      { name: 'Delete', label: { en: 'Delete' }, showedOn: 'both', selectionRule: '>0', offset: 0, isDefault: true },
    ]),
    getLookupReference: vi.fn(),
    executeQueryByName: vi.fn(),
  };
  TestBed.configureTestingModule({
    providers: [
      provideNoopAnimations(),
      provideRouter(routes),
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: SparkService, useValue: service },
      { provide: SparkLanguageService, useValue: { t: (k: string) => k } },
      { provide: SPARK_ATTRIBUTE_RENDERERS, useValue: [] },
      provideSparkDetailPanels({ id: 'test-panel', component: TestPanel }),
      provideSparkDetailActions({ id: 'test-action', component: TestAction }),
    ],
  });
  const harness = await RouterTestingHarness.create();
  return { harness, service };
}

describe('SparkPoDetailComponent — SPARK_DETAIL_PANELS / SPARK_DETAIL_ACTIONS (#460)', () => {
  it('renders registered panels and actions with the page context', async () => {
    const { harness } = await setup({ canViewHistory: true });
    await harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
    await settle(harness.fixture);

    const el: HTMLElement = harness.routeNativeElement!;
    expect(el.querySelector('.test-panel')?.textContent).toContain('panel Alice');
    expect(el.querySelector('.test-action')).not.toBeNull();

    const ctx = seen.at(-1)!;
    expect(ctx.type).toBe('person');
    expect(ctx.id).toBe('people/1');
    expect(ctx.entityType.name).toBe('Person');
    expect(ctx.permissions?.canViewHistory).toBe(true);
    expect(ctx.deleted).toBeNull();
  });

  it('loads a row with ?deleted=only through the deleted mode and hides Edit, Delete and custom actions', async () => {
    const { harness, service } = await setup();
    const c = await harness.navigateByUrl('/po/person/people%2F1?deleted=only', SparkPoDetailComponent);
    await settle(harness.fixture);

    expect(service.get).toHaveBeenCalledWith('person', 'people/1', { deleted: 'only' });
    expect(c.deletedMode()).toBe('only');
    expect(c.canEdit()).toBe(false);
    expect(c.canDelete()).toBe(false);
    expect(c.customActions()).toEqual([]);
    expect(seen.at(-1)?.deleted).toBe('only');
  });

  it('tells its sub-query cards the parent is deleted, so they offer no New, Delete or custom action', async () => {
    const { harness, service } = await setup();
    const withAnswers = { ...personType, queries: ['person-notes'] };
    service.getEntityTypes.mockResolvedValue([withAnswers]);
    Object.assign(service, {
      getQueries: vi.fn().mockResolvedValue([]),
      getQuery: vi.fn().mockResolvedValue(null),
      executeQuery: vi.fn().mockResolvedValue({ columns: [], items: [], totalItems: 0, skip: 0, take: 50 }),
    });

    await harness.navigateByUrl('/po/person/people%2F1?deleted=only', SparkPoDetailComponent);
    await settle(harness.fixture);
    const cards = () => harness.fixture.debugElement.queryAll(By.directive(SparkQueryCardComponent))
      .map(d => (d.componentInstance as SparkQueryCardComponent).parentDeleted());
    expect(cards()).toEqual([true]);

    await harness.navigateByUrl('/po/person/people%2F1', SparkPoDetailComponent);
    await settle(harness.fixture);
    expect(cards()).toEqual([false]);
  });

  it('keeps the plain two-argument load for a normal page and for ?deleted=exclude', async () => {
    const { harness, service } = await setup();
    await harness.navigateByUrl('/po/person/people%2F1?deleted=exclude', SparkPoDetailComponent);
    await settle(harness.fixture);
    expect(service.get).toHaveBeenCalledWith('person', 'people/1');
    expect(service.get.mock.calls.every((call: unknown[]) => call.length === 2)).toBe(true);
  });

  it('context.reload() re-reads the object with the same mode', async () => {
    const { harness, service } = await setup();
    await harness.navigateByUrl('/po/person/people%2F1?deleted=include', SparkPoDetailComponent);
    await settle(harness.fixture);
    service.get.mockClear();

    await seen.at(-1)!.reload();
    expect(service.get).toHaveBeenCalledWith('person', 'people/1', { deleted: 'include' });
  });
});
