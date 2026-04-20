import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { describe, expect, it, vi } from 'vitest';

import { SparkSubQueryComponent } from './spark-sub-query.component';
import { SparkService } from '@mintplayer/ng-spark/services';
import { SPARK_ATTRIBUTE_RENDERERS } from '@mintplayer/ng-spark/renderers';
import {
  EntityType,
  PersistentObject,
  ShowedOn,
  SparkQuery,
} from '@mintplayer/ng-spark/models';

const lineType: EntityType = {
  id: 't-line',
  name: 'Line',
  alias: 'line',
  clrType: 'Test.Line',
  attributes: [
    {
      id: 'a-sku', name: 'Sku', dataType: 'string',
      isRequired: false, isVisible: true, isReadOnly: false,
      order: 1, showedOn: ShowedOn.Query,
    } as any,
    {
      id: 'a-detail', name: 'DetailOnly', dataType: 'string',
      isRequired: false, isVisible: true, isReadOnly: false,
      order: 2, showedOn: ShowedOn.PersistentObject,
    } as any,
    {
      id: 'a-hidden', name: 'Hidden', dataType: 'string',
      isRequired: false, isVisible: false, isReadOnly: false,
      order: 3, showedOn: ShowedOn.Query,
    } as any,
  ],
} as any;

const linesQuery: SparkQuery = {
  id: 'q-lines',
  name: 'Lines',
  alias: 'lines',
  source: 'Database.Lines',
  sortColumns: [{ property: 'Sku', direction: 'asc' }],
  renderMode: 'Pagination',
  entityType: 'Line',
  isStreamingQuery: false,
} as any;

const samplePage = {
  data: [
    { id: 'lines/1', name: 'SKU-1', objectTypeId: 't-line', attributes: [] } as any,
    { id: 'lines/2', name: 'SKU-2', objectTypeId: 't-line', attributes: [] } as any,
  ],
  totalRecords: 2,
};

async function flush(): Promise<void> {
  for (let i = 0; i < 5; i++) {
    await new Promise<void>(r => setTimeout(r, 0));
  }
}

function createComponent(serviceOverrides: Partial<SparkService> = {}) {
  const service: any = {
    getQuery: vi.fn().mockResolvedValue(linesQuery),
    getEntityTypes: vi.fn().mockResolvedValue([lineType]),
    getPermissions: vi.fn().mockResolvedValue({ canRead: true, canCreate: true, canEdit: true, canDelete: true }),
    executeQuery: vi.fn().mockResolvedValue(samplePage),
    getLookupReference: vi.fn().mockResolvedValue({ name: 'dummy', values: [] } as any),
    ...serviceOverrides,
  };

  TestBed.configureTestingModule({
    providers: [
      provideNoopAnimations(),
      provideRouter([]),
      { provide: SparkService, useValue: service },
      { provide: SPARK_ATTRIBUTE_RENDERERS, useValue: [] },
    ],
  });

  const fixture = TestBed.createComponent(SparkSubQueryComponent);
  fixture.componentRef.setInput('queryId', 'q-lines');
  fixture.componentRef.setInput('parentId', 'orders/1');
  fixture.componentRef.setInput('parentType', 'order');
  return { fixture, component: fixture.componentInstance, service };
}

describe('SparkSubQueryComponent', () => {
  it('loads query + entity types + permissions on effect fire (Pagination mode)', async () => {
    const { fixture, component, service } = createComponent();
    fixture.detectChanges();
    await flush();

    expect(service.getQuery).toHaveBeenCalledWith('q-lines');
    expect(component.query()?.id).toBe('q-lines');
    expect(component.entityType()?.name).toBe('Line');
    expect(component.canRead()).toBe(true);
    expect(component.loading()).toBe(false);
  });

  it('Pagination mode: executeQuery is called with parent context and page metadata is stored', async () => {
    const { fixture, component, service } = createComponent();
    fixture.detectChanges();
    await flush();

    expect(service.executeQuery).toHaveBeenCalledOnce();
    const [queryId, opts] = (service.executeQuery as any).mock.calls[0];
    expect(queryId).toBe('q-lines');
    expect(opts.parentId).toBe('orders/1');
    expect(opts.parentType).toBe('order');
    expect(opts.skip).toBe(0);
    expect(opts.take).toBe(10);
    expect(component.paginationData()?.totalRecords).toBe(2);
    expect(component.paginationData()?.totalPages).toBe(1);
  });

  it('VirtualScrolling mode initializes virtualDataSource instead of paginationData', async () => {
    const virtualQuery = { ...linesQuery, renderMode: 'VirtualScrolling' } as any;
    const { fixture, component } = createComponent({
      getQuery: vi.fn().mockResolvedValue(virtualQuery),
    });
    fixture.detectChanges();
    await flush();

    expect(component.virtualDataSource()).not.toBeNull();
    expect(component.paginationData()).toBeUndefined();
  });

  it('visibleAttributes keeps Query-showed visible attrs and excludes detail-only + hidden', async () => {
    const { fixture, component } = createComponent();
    fixture.detectChanges();
    await flush();

    const names = component.visibleAttributes().map(a => a.name);
    expect(names).toEqual(['Sku']);
  });

  it('initial sortColumns map desc/asc to descending/ascending', async () => {
    const q = { ...linesQuery, sortColumns: [{ property: 'Sku', direction: 'desc' }] } as any;
    const { fixture, component } = createComponent({
      getQuery: vi.fn().mockResolvedValue(q),
    });
    fixture.detectChanges();
    await flush();

    expect(component.settings().sortColumns[0]).toEqual({ property: 'Sku', direction: 'descending' });
  });

  it('onSettingsChange in Pagination mode re-fetches the page', async () => {
    const { fixture, component, service } = createComponent();
    fixture.detectChanges();
    await flush();
    (service.executeQuery as any).mockClear();

    component.onSettingsChange();
    await flush();

    expect(service.executeQuery).toHaveBeenCalledOnce();
  });

  it('onSettingsChange in VirtualScrolling mode rebuilds the data source', async () => {
    const virtualQuery = { ...linesQuery, renderMode: 'VirtualScrolling' } as any;
    const { fixture, component } = createComponent({
      getQuery: vi.fn().mockResolvedValue(virtualQuery),
    });
    fixture.detectChanges();
    await flush();
    const firstSource = component.virtualDataSource();

    component.onSettingsChange();

    expect(component.virtualDataSource()).not.toBe(firstSource);
  });

  it('error path: when getQuery rejects, paginationData clears and loading resolves to false', async () => {
    const { fixture, component } = createComponent({
      getQuery: vi.fn().mockRejectedValue(new Error('boom')),
    });
    fixture.detectChanges();
    await flush();

    expect(component.paginationData()).toBeUndefined();
    expect(component.loading()).toBe(false);
  });

  it('onSettingsChange is a no-op before the query has resolved', async () => {
    const { fixture, component, service } = createComponent({
      getQuery: vi.fn(() => new Promise<SparkQuery>(() => {})),
    });
    fixture.detectChanges();

    component.onSettingsChange();

    expect(service.executeQuery).not.toHaveBeenCalled();
  });

  it('entity type resolves by name OR by lowercased alias', async () => {
    const aliasQuery = { ...linesQuery, entityType: 'line' } as any;
    const { fixture, component } = createComponent({
      getQuery: vi.fn().mockResolvedValue(aliasQuery),
    });
    fixture.detectChanges();
    await flush();

    expect(component.entityType()?.id).toBe('t-line');
  });
});
