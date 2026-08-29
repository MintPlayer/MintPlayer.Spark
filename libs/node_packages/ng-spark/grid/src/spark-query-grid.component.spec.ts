import { Component, input } from '@angular/core';
import { TestBed, ComponentFixture } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it, vi, beforeEach } from 'vitest';

import { SparkQueryGridComponent } from './spark-query-grid.component';
import { SparkService, SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SPARK_ATTRIBUTE_RENDERERS } from '@mintplayer/ng-spark/renderers';
import { EntityType, QueryResultItem, ShowedOn, SparkQuery } from '@mintplayer/ng-spark/models';

/**
 * These carry over from the two components this one replaces. They are not fresh coverage: each
 * pins a bug that was fixed once and drifted — the first-column link gate, permission state
 * surviving a failed reload, a swallowed fetch failure, renderer input filtering.
 */

const personType: EntityType = {
  id: 't-person',
  name: 'Person',
  alias: 'person',
  clrType: 'Test.Person',
  attributes: [
    {
      id: 'a-first', name: 'FirstName', dataType: 'string',
      isVisible: true, isReadOnly: false, isRequired: false,
      order: 1, showedOn: ShowedOn.Query | ShowedOn.QueryResultItem,
    } as any,
    {
      id: 'a-internal', name: 'Internal', dataType: 'string',
      isVisible: false, isReadOnly: false, isRequired: false,
      order: 2, showedOn: ShowedOn.Query,
    } as any,
    {
      id: 'a-detail-only', name: 'DetailOnly', dataType: 'string',
      isVisible: true, isReadOnly: false, isRequired: false,
      order: 3, showedOn: ShowedOn.QueryResultItem,
    } as any,
  ],
} as any;

const allPeopleQuery: SparkQuery = {
  id: 'q-all',
  name: 'AllPeople',
  source: 'Database.People',
  alias: 'allpeople',
  entityType: 'Person',
  sortColumns: [],
  renderMode: 'Standard',
  isStreamingQuery: false,
} as any;

// The shape the server sends: columns once, then id + values per row (#327 M4).
const sampleColumns = [
  { name: 'FirstName', dataType: 'string', order: 1 } as any,
];

const samplePage = {
  columns: sampleColumns,
  items: [{ id: 'people/1', breadcrumb: 'Alice', values: [{ key: 'FirstName', value: 'Alice' }] } as any],
  totalItems: 1,
};

/**
 * The real SparkLanguageService fetches `/spark/culture` on construction, and vitest rejects
 * unhandled requests.
 */
const langStub = { t: (k: string) => k, resolve: (v: any) => (typeof v === 'string' ? v : v?.en ?? '') };

function makeService(overrides: Partial<Record<string, unknown>> = {}) {
  return {
    getEntityTypes: vi.fn().mockResolvedValue([personType]),
    getQueries: vi.fn().mockResolvedValue([allPeopleQuery]),
    getQuery: vi.fn().mockResolvedValue(allPeopleQuery),
    getPermissions: vi.fn().mockResolvedValue({ canQuery: true, canRead: true, canCreate: true, canEdit: true, canDelete: true }),
    getCustomActions: vi.fn().mockResolvedValue([]),
    executeQuery: vi.fn().mockResolvedValue(samplePage),
    executeCustomAction: vi.fn().mockResolvedValue(undefined),
    getLookupReference: vi.fn().mockResolvedValue({ values: [] }),
    ...overrides,
  } as any;
}

/**
 * Settle the component, not just the fixture.
 *
 * `loadData` awaits twice — the query and the entity types, and only then the permissions and the
 * custom actions. A single `whenStable()` flushes the first level and returns while the second is
 * still pending, so permissions, actions and the fetch all appear to be missing. Draining until
 * the queue is quiet is the honest wait.
 */
async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  for (let i = 0; i < 5; i++) {
    await fixture.whenStable();
    await Promise.resolve();
    fixture.detectChanges();
  }
  await fixture.whenStable();
}

async function setup(overrides: Partial<Record<string, unknown>> = {}, inputs: Record<string, unknown> = {}) {
  const service = makeService(overrides);
  TestBed.configureTestingModule({
    providers: [
      provideNoopAnimations(),
      provideRouter([]),
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: SparkService, useValue: service },
      { provide: SparkLanguageService, useValue: langStub },
      { provide: SPARK_ATTRIBUTE_RENDERERS, useValue: [] },
    ],
  });
  const fixture: ComponentFixture<SparkQueryGridComponent> = TestBed.createComponent(SparkQueryGridComponent);
  fixture.componentRef.setInput('queryId', 'q-all');
  for (const [k, v] of Object.entries(inputs)) fixture.componentRef.setInput(k, v);
  fixture.detectChanges();
  await settle(fixture);
  return { fixture, c: fixture.componentInstance, service };
}

describe('SparkQueryGridComponent', () => {
  beforeEach(() => TestBed.resetTestingModule());

  it('resolves the query and its entity type from the declared entityType name', async () => {
    const { c, service } = await setup();

    expect(service.getQuery).toHaveBeenCalledWith('q-all');
    expect(c.query()?.id).toBe('q-all');
    expect(c.entityType()?.name).toBe('Person');
  });

  /**
   * The divergence that made this one component worth building. The sub-query grid matched on the
   * declared `entityType` alone, so a `Database.*` query without one rendered as an empty card —
   * no columns, no rows, no error — while the identical query rendered correctly as a page.
   */
  it('falls back to the source name when the query declares no entityType', async () => {
    const undeclared = { ...allPeopleQuery, entityType: undefined } as any;
    const { c } = await setup({ getQuery: vi.fn().mockResolvedValue(undeclared) });

    expect(c.entityType()?.name).toBe('Person');
  });

  it('hydrates canRead and canCreate from getPermissions', async () => {
    const { c } = await setup({
      getPermissions: vi.fn().mockResolvedValue({ canQuery: true, canRead: true, canCreate: false, canEdit: false, canDelete: false }),
    });

    expect(c.canRead()).toBe(true);
    expect(c.canCreate()).toBe(false);
  });

  it('executes the query on load and exposes the result count', async () => {
    const { c, service } = await setup();

    expect(service.executeQuery).toHaveBeenCalledOnce();
    expect(service.executeQuery.mock.calls[0][1].skip).toBe(0);
    expect(c.resultCount()).toBe(1);
  });

  it(`renders the columns the result declares, not the entity type's attributes`, async () => {
    // The visible-column rule (isVisible && ShowedOn.Query) moved to the server, which is also
    // where the sort-column allow-list is checked — so both now derive from one place.
    const { c } = await setup();

    expect(c.visibleColumns().map(col => col.name)).toEqual(['FirstName']);
  });

  it('isVirtualScrolling reflects the query renderMode', async () => {
    const virtual = { ...allPeopleQuery, renderMode: 'VirtualScrolling' } as any;
    const { c } = await setup({ getQuery: vi.fn().mockResolvedValue(virtual) });

    expect(c.isVirtualScrolling()).toBe(true);
  });

  it('passes the search term through to the query', async () => {
    const { fixture, service } = await setup();
    service.executeQuery.mockClear();

    fixture.componentRef.setInput('search', 'alice');
    fixture.detectChanges();
    await settle(fixture);

    expect(service.executeQuery).toHaveBeenCalled();
    expect(service.executeQuery.mock.calls.at(-1)![1].search).toBe('alice');
  });

  describe('where the rows come from', () => {
    /**
     * Bound `data` means the host owns the rows. A fetch here would be a second, contradictory
     * source, and `bs-datatable` resolves that by silently preferring `[fetch]`.
     */
    it('does not fetch when data is supplied', async () => {
      const { service } = await setup({}, { data: [] });

      expect(service.executeQuery).not.toHaveBeenCalled();
    });

    /**
     * The race this closes: the grid resolves the query before the host can see it, so waiting to
     * be handed `data` would still have fired one pointless /execute on mount.
     */
    it('does not fetch for a streaming query, even with no data bound', async () => {
      const streaming = { ...allPeopleQuery, isStreamingQuery: true } as any;
      const { service } = await setup({ getQuery: vi.fn().mockResolvedValue(streaming) });

      expect(service.executeQuery).not.toHaveBeenCalled();
    });

    it('an empty array is "no rows", not "fetch for yourself"', async () => {
      const { c, service } = await setup({}, { data: [] });

      expect(service.executeQuery).not.toHaveBeenCalled();
      expect(c.fetchFn()).toBeNull();
    });
  });

  describe('failure', () => {
    it('renders a message and emits when the query cannot be resolved', async () => {
      const { fixture, c } = await setup({
        getQuery: vi.fn().mockRejectedValue({ status: 404 }),
      });

      expect(c.query()).toBeNull();
      expect(c.errorMessage()).toBeTruthy();
      expect(fixture.nativeElement.textContent).toContain('spark.query.unavailable');
    });

    /**
     * A 404 is both "no such query" and "you may not see it", answered with byte-identical bodies
     * so existence is not disclosed (audit M-3). A message naming either one would leak or mislead.
     */
    it('does not distinguish a missing query from a denied one', async () => {
      const { c } = await setup({ getQuery: vi.fn().mockRejectedValue({ status: 404 }) });

      expect(c.errorMessage()).toBe('spark.query.unavailable');
      expect(c.errorMessage()).not.toContain('denied');
      expect(c.errorMessage()).not.toContain('exist');
    });

    /**
     * Leaving these behind let a failed reload build a row link out of the PREVIOUS type and the
     * previous permission.
     */
    it('clears permission and type state when a reload fails', async () => {
      const getQuery = vi.fn().mockResolvedValue(allPeopleQuery);
      const { fixture, c } = await setup({ getQuery });
      expect(c.canRead()).toBe(true);

      getQuery.mockRejectedValue({ status: 404 });
      fixture.componentRef.setInput('queryId', 'q-other');
      fixture.detectChanges();
      await settle(fixture);

      expect(c.canRead()).toBe(false);
      expect(c.entityType()).toBeNull();
    });

    it('stops spinning when no query id is supplied', async () => {
      const { fixture, c } = await setup();
      fixture.componentRef.setInput('queryId', '');
      fixture.detectChanges();
      await settle(fixture);

      expect(c.loading()).toBe(false);
    });
  });

  describe('column renderer inputs (#241/#245)', () => {
    @Component({ selector: 'spec-full-column-renderer', standalone: true, template: '' })
    class FullColumnRenderer {
      value = input<any>();
      attribute = input<any>();
      options = input<Record<string, any>>();
      item = input<any>();
    }
    @Component({ selector: 'spec-value-only-renderer', standalone: true, template: '' })
    class ValueOnlyRenderer {
      value = input<any>();
    }

    const asDetailColumn = { name: 'Coverage', dataType: 'AsDetail', isArray: true, order: 1, rendererOptions: { bar: true } } as any;

    it(`a renderer receives the cell value, the row and the column's options`, async () => {
      const { c } = await setup();
      // A projection is flat: an AsDetail cell carries the child COUNT, not the children, because
      // a row deliberately drags no nested object graph across the wire.
      const row = { id: 'people/1', values: [{ key: 'Coverage', value: 3 }] } as QueryResultItem;

      const inputs = c.getColumnRendererInputs(FullColumnRenderer, row, asDetailColumn);
      expect(inputs['value']).toBe(3);
      expect(inputs['item']).toBe(row);
      expect(inputs['options']).toEqual({ bar: true });
    });

    it('a cell the row does not carry yields undefined rather than throwing', async () => {
      const { c } = await setup();
      const row = { id: 'people/1', values: [] } as QueryResultItem;

      expect(c.getColumnRendererInputs(FullColumnRenderer, row, asDetailColumn)['value']).toBeUndefined();
    });

    it('renderer declaring only value gets a filtered bag (pins the NgComponentOutlet undeclared-input throw)', async () => {
      const { c } = await setup();
      const row = { id: 'people/1', values: [{ key: 'FirstName', value: 'Alice' }] } as QueryResultItem;

      const inputs = c.getColumnRendererInputs(ValueOnlyRenderer, row, sampleColumns[0]);
      expect(Object.keys(inputs)).toEqual(['value']);
      expect(inputs['value']).toBe('Alice');
    });
  });

  describe('rowRoute (#327 §9.2)', () => {
    // rowRouteFor is protected; TypeScript's protection is compile-time only, and reaching it
    // directly is what lets these pin the RULE rather than bs-datatable's rendering.
    const routeFor = (c: SparkQueryGridComponent, row: QueryResultItem) =>
      (c as any).rowRouteFor(row) as unknown[] | null;

    const row = { id: 'people/1', values: [] } as QueryResultItem;

    it(`defaults to the row's own detail page`, async () => {
      const { c } = await setup();

      expect(routeFor(c, row)).toEqual(['/po', c.entityType()?.alias ?? c.entityType()?.id, 'people/1']);
    });

    it(`uses the host's function when one is supplied`, async () => {
      const { c } = await setup({}, { rowRoute: (r: QueryResultItem) => ['/custom', r.id] });

      expect(routeFor(c, row)).toEqual(['/custom', 'people/1']);
    });

    it('suppresses the link for a row the function returns null for', async () => {
      // A grid may mix rows that have a destination with rows that do not — a composed grid
      // listing collections, say, where only some map to a page.
      const { c } = await setup({}, { rowRoute: () => null });

      expect(routeFor(c, row)).toBeNull();
    });

    it('is consulted per row, not once for the grid', async () => {
      const seen: string[] = [];
      const { c } = await setup({}, { rowRoute: (r: QueryResultItem) => { seen.push(r.id); return ['/x', r.id]; } });

      seen.length = 0;   // rendering already called it; the subject here is the per-row call
      routeFor(c, { id: 'people/1', values: [] } as QueryResultItem);
      routeFor(c, { id: 'people/2', values: [] } as QueryResultItem);

      expect(seen).toEqual(['people/1', 'people/2']);
    });

    /**
     * ⚠️ The one that matters. `rowRoute` replaces the DESTINATION; `canRead()` is what decides
     * whether any link renders. A host must not be able to hand itself a link to a row the rights
     * model withheld, so the gate is asserted where it lives — in the template's own condition,
     * ahead of the call.
     */
    /**
     * ⚠️ The one that matters: `rowRoute` replaces the DESTINATION, `canRead()` is the gate.
     *
     * Asserted through the CALL rather than the rendered anchor. `bs-datatable` is a Lit component
     * and renders no row anchors under jsdom, so a `querySelector('a')` assertion here passes
     * whether the gate works or not — the kind of green that means nothing. The template condition
     * is `first && canRead() && rowRouteFor(row)`, so short-circuiting is observable: with Read
     * withheld the function is never reached at all.
     */
    it('is never even consulted when Read is withheld', async () => {
      const asked: string[] = [];
      const { fixture, c } = await setup(
        { getPermissions: vi.fn().mockResolvedValue({ canQuery: true, canRead: false, canCreate: false, canEdit: false, canDelete: false }) },
        { data: [{ id: 'people/1', values: [{ key: 'FirstName', value: 'Alice' }] }], columns: sampleColumns,
          rowRoute: (r: QueryResultItem) => { asked.push(r.id); return ['/anywhere']; } });

      expect(c.canRead()).toBe(false);
      expect(asked).toEqual([]);
      void fixture;
    });

    it('IS consulted once Read is granted — so the test above is not vacuous', async () => {
      // The control. Same fixture, same bound rows, Read granted: the function is reached. Without
      // this, the assertion above would keep passing if the row template stopped rendering at all.
      const asked: string[] = [];
      await setup(
        { getPermissions: vi.fn().mockResolvedValue({ canQuery: true, canRead: true, canCreate: false, canEdit: false, canDelete: false }) },
        { data: [{ id: 'people/1', values: [{ key: 'FirstName', value: 'Alice' }] }], columns: sampleColumns,
          rowRoute: (r: QueryResultItem) => { asked.push(r.id); return ['/custom', r.id]; } });

      expect(asked).toContain('people/1');
    });
  });
  describe('selection -> custom action (#327)', () => {
    const rows: QueryResultItem[] = [
      { id: 'cars/1', values: [{ key: 'LicensePlate', value: '1-ABC' }] },
      { id: 'cars/2', values: [{ key: 'LicensePlate', value: '2-DEF' }] },
      { id: 'cars/3', values: [{ key: 'LicensePlate', value: '3-GHI' }] },
    ];

    const copyAction = {
      name: 'CopyCarsToCompany',
      displayName: { en: 'Copy' },
      showedOn: 'both',
      selectionRule: '>=1',
      refreshOnCompleted: false,
    } as any;

    async function grid(inputs: Record<string, unknown> = {}) {
      return await setup(
        { getCustomActions: vi.fn().mockResolvedValue([copyAction]) },
        { data: rows, columns: sampleColumns, ...inputs });
    }

    it('posts exactly the selected ids, in selection order', async () => {
      const { c, service } = await grid();
      c.selection.set([rows[2], rows[0]]);

      await c.onCustomAction(copyAction);

      expect(service.executeCustomAction).toHaveBeenCalledTimes(1);
      const [, actionName, , ids] = service.executeCustomAction.mock.calls[0];
      expect(actionName).toBe('CopyCarsToCompany');
      expect(ids).toEqual(['cars/3', 'cars/1']);
    });

    it('posts ids, never the row objects', async () => {
      // The wire contract: a row is a projection the server handed out, not a document the client
      // may hand back. Posting rows is what #327 removed.
      const { c, service } = await grid();
      c.selection.set([rows[0]]);

      await c.onCustomAction(copyAction);

      const [, , , ids] = service.executeCustomAction.mock.calls[0];
      expect(ids).toEqual(['cars/1']);
      expect(ids.every((v: unknown) => typeof v === 'string')).toBe(true);
    });

    it('sends the sub-query container as parentId/parentType', async () => {
      // The flow this whole thing exists for: a grid on a Company detail page. Without it the
      // action cannot tell which company it was invoked from - the grid knew (it filters the rows
      // by that parent) and used to drop the fact on the way out.
      const { c, service } = await grid({ parentId: 'companies/1', parentType: 'Company' });
      c.selection.set([rows[0], rows[1]]);

      await c.onCustomAction(copyAction);

      const [typeId, , parent, ids, queryParent, queryId] = service.executeCustomAction.mock.calls[0];
      expect(typeId).toBe(c.entityType()!.id);
      expect(ids).toEqual(['cars/1', 'cars/2']);
      expect(queryParent).toEqual({ id: 'companies/1', type: 'Company' });
      // NOT the `parent` argument: that means an object of the action's own type, and the server
      // loads it under that type - so a Company id there would refuse.
      expect(parent).toBeUndefined();
      // And the query, so the server re-runs it narrowed to those ids rather than re-deriving the
      // rows from documents - which is what keeps index-computed columns populated.
      expect(queryId).toBe('q-all');
    });

    it('sends the query the rows came from', async () => {
      const { c, service } = await grid();
      c.selection.set([rows[0]]);

      await c.onCustomAction(copyAction);

      const [, , , , , queryId] = service.executeCustomAction.mock.calls[0];
      expect(queryId).toBe('q-all');
    });

    it('sends no container from a top-level query page', async () => {
      const { c, service } = await grid();
      c.selection.set([rows[0]]);

      await c.onCustomAction(copyAction);

      const [, , , , queryParent] = service.executeCustomAction.mock.calls[0];
      expect(queryParent).toBeUndefined();
    });

    it('sends no container when only one half of the parent is bound', async () => {
      // Matches executeQuery and the execute endpoint, both of which resolve a parent only when
      // id and type are both present. Half a parent is not a parent.
      const { c, service } = await grid({ parentId: 'companies/1' });
      c.selection.set([rows[0]]);

      await c.onCustomAction(copyAction);

      const [, , , , queryParent] = service.executeCustomAction.mock.calls[0];
      expect(queryParent).toBeUndefined();
    });

    it('surfaces a failure instead of silently doing nothing', async () => {
      const { c, service } = await grid();
      service.executeCustomAction.mockRejectedValue({ error: { error: 'Not found' } });
      c.selection.set([rows[0]]);

      await c.onCustomAction(copyAction);

      expect(c.errorMessage()).toBe('Not found');
    });

    it('enables the action only once its selection rule is satisfied', async () => {
      const { c } = await grid();

      expect(c.isActionEnabled(copyAction)).toBe(false);
      c.selection.set([rows[0]]);
      expect(c.isActionEnabled(copyAction)).toBe(true);
    });
  });

});
