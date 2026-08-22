import { Component, input } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { describe, expect, it, vi } from 'vitest';

import { SparkSubQueryComponent } from './spark-sub-query.component';
import { SparkService, SparkLanguageService } from '@mintplayer/ng-spark/services';
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
    getPermissions: vi.fn().mockResolvedValue({ canQuery: true, canRead: true, canCreate: true, canEdit: true, canDelete: true }),
    executeQuery: vi.fn().mockResolvedValue(samplePage),
    getLookupReference: vi.fn().mockResolvedValue({ name: 'dummy', values: [] } as any),
    // The component loads the query's actions alongside its permissions, so an unstubbed
    // method rejects the Promise.all and the whole load fails.
    getCustomActions: vi.fn().mockResolvedValue([]),
    ...serviceOverrides,
  };

  TestBed.configureTestingModule({
    providers: [
      provideNoopAnimations(),
      provideRouter([]),
      { provide: SparkService, useValue: service },
      // The component resolves its own labels now (the priority-nav action bar), and the real
      // SparkLanguageService fetches /spark/culture and /spark/translations on construction.
      // Unmocked those reject into nothing and vitest reports unhandled errors while every
      // assertion still passes -- same stub as spark-po-detail.component.spec.ts.
      { provide: SparkLanguageService, useValue: { t: (k: string) => k } },
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

  it('builds a fetch callback that calls executeQuery with parent context + paging and maps the response', async () => {
    const { fixture, component, service } = createComponent();
    fixture.detectChanges();
    await flush();

    const fetch = component.fetchFn();
    expect(fetch).toBeTruthy();

    const res = await fetch!({ page: 1, perPage: 10, sortColumns: [] });
    const [queryId, opts] = (service.executeQuery as any).mock.calls.at(-1);
    expect(queryId).toBe('q-lines');
    expect(opts.parentId).toBe('orders/1');
    expect(opts.parentType).toBe('order');
    expect(opts.skip).toBe(0);
    expect(opts.take).toBe(10);
    expect(res.totalRecords).toBe(2);
    expect(res.totalPages).toBe(1);
  });

  it('VirtualScrolling mode still uses the fetch callback (virtual is just a flag)', async () => {
    const virtualQuery = { ...linesQuery, renderMode: 'VirtualScrolling' } as any;
    const { fixture, component } = createComponent({
      getQuery: vi.fn().mockResolvedValue(virtualQuery),
    });
    fixture.detectChanges();
    await flush();

    expect(component.isVirtualScrolling()).toBe(true);
    expect(component.fetchFn()).toBeTruthy();
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

  it('error path: when getQuery rejects, fetchFn stays null and loading resolves to false', async () => {
    const { fixture, component } = createComponent({
      getQuery: vi.fn().mockRejectedValue(new Error('boom')),
    });
    fixture.detectChanges();
    await flush();

    expect(component.fetchFn()).toBeNull();
    expect(component.loading()).toBe(false);
  });

  describe('without a parent', () => {
    // A query does not have to be a detail of something: a page can host a grid
    // that stands on its own. Requiring a parent made that shape load nothing at
    // all — no request, no error, no log — so these assert the loading actually
    // happens, not merely that the inputs are optional.
    function createParentless(serviceOverrides: Record<string, unknown> = {}) {
      const made = createComponent(serviceOverrides);
      made.fixture.componentRef.setInput('parentId', '');
      made.fixture.componentRef.setInput('parentType', '');
      return made;
    }

    it('loads the query when no parent is given', async () => {
      const { fixture, component, service } = createParentless();
      fixture.detectChanges();
      await flush();

      expect(service.getQuery).toHaveBeenCalledWith('q-lines');
      expect(component.query()?.id).toBe('q-lines');
      expect(component.loading()).toBe(false);
      expect(component.fetchFn()).not.toBeNull();
    });

    it('omits parentId and parentType from the execute call', async () => {
      const { fixture, component, service } = createParentless();
      fixture.detectChanges();
      await flush();

      await component.fetchFn()!({ page: 1, perPage: 10, sortColumns: [] } as never);

      const opts = (service.executeQuery as ReturnType<typeof vi.fn>).mock.calls[0][1];
      expect(opts.parentId).toBeFalsy();
      expect(opts.parentType).toBeFalsy();
    });

    it('still loads when only one half of the parent is given', async () => {
      // Half a parent is not a parent. The execute endpoint resolves one only
      // when both are present, so the grid must not be held hostage by the other.
      const { fixture, component, service } = createComponent();
      fixture.componentRef.setInput('parentType', '');
      fixture.detectChanges();
      await flush();

      expect(service.getQuery).toHaveBeenCalledWith('q-lines');
      expect(component.fetchFn()).not.toBeNull();
    });
  });

  it('fetchFn is null before the query has resolved', async () => {
    const { fixture, component } = createComponent({
      getQuery: vi.fn(() => new Promise<SparkQuery>(() => {})),
    });
    fixture.detectChanges();

    expect(component.fetchFn()).toBeNull();
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

  describe('column renderer inputs (#241/#245)', () => {
    @Component({ selector: 'spec-subq-full-renderer', standalone: true, template: '' })
    class FullColumnRenderer {
      value = input<any>();
      attribute = input<any>();
      options = input<Record<string, any>>();
      item = input<any>();
    }
    @Component({ selector: 'spec-subq-value-only-renderer', standalone: true, template: '' })
    class ValueOnlyRenderer {
      value = input<any>();
    }

    it('AsDetail attribute: renderer receives the nested PO as value and the row as item', () => {
      const { component } = createComponent();
      const nested = { id: 'cov/1', objectTypeId: 't-cov', attributes: [] } as any;
      const row = { id: 'lines/1', attributes: [{ name: 'Coverage', value: null, object: nested }] } as PersistentObject;
      const attr = { name: 'Coverage', dataType: 'AsDetail' } as any;

      const inputs = component.getColumnRendererInputs(FullColumnRenderer, row, attr);
      expect(inputs['value']).toBe(nested);
      expect(inputs['item']).toBe(row);
    });

    it('renderer declaring only value gets a filtered bag', () => {
      const { component } = createComponent();
      const row = { id: 'lines/1', attributes: [{ name: 'Sku', value: 'SKU-1' }] } as PersistentObject;

      const inputs = component.getColumnRendererInputs(ValueOnlyRenderer, row, lineType.attributes[0]);
      expect(Object.keys(inputs)).toEqual(['value']);
      expect(inputs['value']).toBe('SKU-1');
    });
  });
});
