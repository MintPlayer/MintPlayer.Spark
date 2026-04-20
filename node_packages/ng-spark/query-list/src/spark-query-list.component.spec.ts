import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Routes } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it, vi } from 'vitest';

import { SparkQueryListComponent } from './spark-query-list.component';
import { SparkService, SparkStreamingService } from '@mintplayer/ng-spark/services';
import { SPARK_ATTRIBUTE_RENDERERS } from '@mintplayer/ng-spark/renderers';
import { EntityType, PersistentObject, ShowedOn, SparkQuery } from '@mintplayer/ng-spark/models';

@Component({ standalone: true, template: '' })
class StubComponent {}

const personType: EntityType = {
  id: 't-person',
  name: 'Person',
  alias: 'person',
  clrType: 'Test.Person',
  attributes: [
    {
      id: 'a-first', name: 'FirstName', dataType: 'string',
      isVisible: true, isReadOnly: false, isRequired: false,
      order: 1, showedOn: ShowedOn.Query | ShowedOn.PersistentObject,
    } as any,
    {
      id: 'a-internal', name: 'Internal', dataType: 'string',
      isVisible: false, isReadOnly: false, isRequired: false,
      order: 2, showedOn: ShowedOn.Query,
    } as any,
    {
      id: 'a-detail-only', name: 'DetailOnly', dataType: 'string',
      isVisible: true, isReadOnly: false, isRequired: false,
      order: 3, showedOn: ShowedOn.PersistentObject,
    } as any,
  ],
} as any;

const allPeopleQuery: SparkQuery = {
  id: 'q-all',
  name: 'AllPeople',
  source: 'Database.People',
  alias: 'allpeople',
  sortColumns: [],
  renderMode: 'Standard',
  isStreamingQuery: false,
} as any;

const routes: Routes = [
  { path: 'query/:queryId', component: SparkQueryListComponent },
  { path: 'po/:type', component: SparkQueryListComponent },
  { path: 'po/:type/new', component: StubComponent },
];

const samplePage = {
  data: [
    { id: 'people/1', name: 'Alice', objectTypeId: 't-person', attributes: [] } as any,
  ],
  totalRecords: 1,
};

async function setup(serviceOverrides: Partial<SparkService> = {}) {
  const service: any = {
    getEntityTypes: vi.fn().mockResolvedValue([personType]),
    getQueries: vi.fn().mockResolvedValue([allPeopleQuery]),
    getQuery: vi.fn().mockResolvedValue(allPeopleQuery),
    getPermissions: vi.fn().mockResolvedValue({ canRead: true, canCreate: true, canUpdate: true, canDelete: true }),
    executeQuery: vi.fn().mockResolvedValue(samplePage),
    getLookupReference: vi.fn().mockResolvedValue({ values: [] }),
    ...serviceOverrides,
  };
  const streaming: any = {
    connect: vi.fn(),
    disconnect: vi.fn(),
  };
  TestBed.configureTestingModule({
    providers: [
      provideNoopAnimations(),
      provideRouter(routes),
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: SparkService, useValue: service },
      { provide: SparkStreamingService, useValue: streaming },
      { provide: SPARK_ATTRIBUTE_RENDERERS, useValue: [] },
    ],
  });
  const harness = await RouterTestingHarness.create();
  return { harness, service };
}

describe('SparkQueryListComponent', () => {
  it('loads query by queryId route param and resolves matching entity type', async () => {
    const { harness, service } = await setup();
    const c = await harness.navigateByUrl('/query/q-all', SparkQueryListComponent);
    await harness.fixture.whenStable();

    expect(service.getQuery).toHaveBeenCalledWith('q-all');
    expect(c.query()?.id).toBe('q-all');
    expect(c.entityType()?.name).toBe('Person');
  });

  it('loads query by entity type alias and finds the matching query for it', async () => {
    const { harness, service } = await setup();
    const c = await harness.navigateByUrl('/po/person', SparkQueryListComponent);
    await harness.fixture.whenStable();

    expect(service.getEntityTypes).toHaveBeenCalled();
    expect(service.getQueries).toHaveBeenCalled();
    expect(c.entityType()?.name).toBe('Person');
    expect(c.query()?.id).toBe('q-all');
  });

  it('hydrates canRead/canCreate from getPermissions', async () => {
    const { harness } = await setup({
      getPermissions: vi.fn().mockResolvedValue({ canRead: true, canCreate: false, canUpdate: false, canDelete: false }),
    });
    const c = await harness.navigateByUrl('/query/q-all', SparkQueryListComponent);
    await harness.fixture.whenStable();

    expect(c.canRead()).toBe(true);
    expect(c.canCreate()).toBe(false);
  });

  it('executes the query on initial load and stores the page in paginationData', async () => {
    const { harness, service } = await setup();
    const c = await harness.navigateByUrl('/query/q-all', SparkQueryListComponent);
    await harness.fixture.whenStable();

    expect(service.executeQuery).toHaveBeenCalledOnce();
    const page = c.paginationData();
    expect(page?.data).toHaveLength(1);
    expect(page?.totalRecords).toBe(1);
  });

  it('visibleAttributes filters out non-visible and detail-only attributes', async () => {
    const { harness } = await setup();
    const c = await harness.navigateByUrl('/query/q-all', SparkQueryListComponent);
    await harness.fixture.whenStable();

    const visible = c.visibleAttributes();
    expect(visible.map((a: any) => a.name)).toEqual(['FirstName']);
  });

  it('isVirtualScrolling reflects query renderMode', async () => {
    const virtualQuery = { ...allPeopleQuery, id: 'q-virt', renderMode: 'VirtualScrolling' } as any;
    const { harness } = await setup({
      getQuery: vi.fn().mockResolvedValue(virtualQuery),
    });
    const c = await harness.navigateByUrl('/query/q-virt', SparkQueryListComponent);
    await harness.fixture.whenStable();

    expect(c.isVirtualScrolling()).toBe(true);
  });

  it('onSearchChange refetches with the current searchTerm', async () => {
    const { harness, service } = await setup();
    const c = await harness.navigateByUrl('/query/q-all', SparkQueryListComponent);
    await harness.fixture.whenStable();
    (service.executeQuery as any).mockClear();

    c.searchTerm = 'alice';
    c.onSearchChange();
    await new Promise<void>((r) => setTimeout(r, 0));

    expect(service.executeQuery).toHaveBeenCalledOnce();
    const opts = (service.executeQuery as any).mock.calls[0][1];
    expect(opts.search).toBe('alice');
  });

  it('clearSearch resets searchTerm and triggers a reload', async () => {
    const { harness, service } = await setup();
    const c = await harness.navigateByUrl('/query/q-all', SparkQueryListComponent);
    await harness.fixture.whenStable();
    c.searchTerm = 'alice';
    (service.executeQuery as any).mockClear();

    c.clearSearch();
    await new Promise<void>((r) => setTimeout(r, 0));

    expect(c.searchTerm).toBe('');
    expect(service.executeQuery).toHaveBeenCalledOnce();
    const opts = (service.executeQuery as any).mock.calls[0][1];
    expect(opts.search).toBeUndefined();
  });

  it('rowClicked output emits when a row is clicked', async () => {
    const { harness } = await setup();
    const c = await harness.navigateByUrl('/query/q-all', SparkQueryListComponent);
    await harness.fixture.whenStable();

    const handler = vi.fn();
    c.rowClicked.subscribe(handler);

    const item: PersistentObject = { id: 'people/1', name: 'A', objectTypeId: 't-person', attributes: [] } as any;
    c.rowClicked.emit(item);

    expect(handler).toHaveBeenCalledWith(item);
  });
});
