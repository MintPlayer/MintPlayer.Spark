import { Component, input } from '@angular/core';
import { TestBed, ComponentFixture } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it, vi, beforeEach } from 'vitest';

import { DatatableSettings } from '@mintplayer/ng-bootstrap/datatable';
import { SparkQueryGridComponent } from './spark-query-grid.component';
import { SparkService, SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SPARK_ATTRIBUTE_RENDERERS } from '@mintplayer/ng-spark/renderers';
import { EntityType, QueryResultItem, ShowedOn, SparkQuery } from '@mintplayer/ng-spark/models';
import { settle } from '../../src/test-utils';

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
    getDistinctValues: vi.fn().mockResolvedValue({ matching: [], remaining: [], hasMore: false }),
    ...overrides,
  } as any;
}

async function setup(overrides: Partial<Record<string, unknown>> = {}, inputs: Record<string, unknown> = {}, renderers: any[] = []) {
  const service = makeService(overrides);
  TestBed.configureTestingModule({
    providers: [
      provideNoopAnimations(),
      provideRouter([]),
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: SparkService, useValue: service },
      { provide: SparkLanguageService, useValue: langStub },
      { provide: SPARK_ATTRIBUTE_RENDERERS, useValue: renderers },
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

  describe('search (#460 M15, D17 addendum)', () => {
    it('sends the search together with the sub-query parent', async () => {
      const { fixture, service } = await setup({}, { parentId: 'companies/1', parentType: 'Company' });
      service.executeQuery.mockClear();

      fixture.componentRef.setInput('search', 'ali');
      await settle(fixture);

      expect(service.executeQuery.mock.calls.at(-1)![1]).toEqual(expect.objectContaining({
        search: 'ali', parentId: 'companies/1', parentType: 'Company', skip: 0,
      }));
    });

    it('clears the selection when the search changes, so the chip counts what is on screen', async () => {
      const { fixture, c } = await setup();
      c.selection.set([{ id: 'people/1', values: [] } as QueryResultItem]);

      fixture.componentRef.setInput('search', 'bob');
      await settle(fixture);

      expect(c.selection()).toEqual([]);
    });

    it('lists a column filter\'s values from the searched rows only', async () => {
      const { fixture, c, service } = await setup({}, { parentId: 'companies/1', parentType: 'Company' });
      fixture.componentRef.setInput('search', 'ali');
      await settle(fixture);

      await (c as any).distinctsFn({ column: 'FirstName', search: 'x', signal: new AbortController().signal });

      expect(service.getDistinctValues).toHaveBeenCalledWith('q-all', 'FirstName', expect.objectContaining({
        search: 'x', querySearch: 'ali', parentId: 'companies/1', parentType: 'Company',
      }));
    });

    it('sends no grid search to the value list when there is none', async () => {
      const { c, service } = await setup();

      await (c as any).distinctsFn({ column: 'FirstName', search: '', signal: new AbortController().signal });

      expect(service.getDistinctValues.mock.calls.at(-1)![2].querySearch).toBeUndefined();
    });
  });

  describe('soft-deletion mode (#460)', () => {
    it('sends nothing for the default mode', async () => {
      const { service } = await setup();
      expect('deleted' in service.executeQuery.mock.calls[0][1]).toBe(false);
    });

    it('sends the recycle-bin mode, refetches on change and links rows with ?deleted', async () => {
      const { fixture, c, service } = await setup();
      service.executeQuery.mockClear();

      fixture.componentRef.setInput('deleted', 'only');
      fixture.detectChanges();
      await settle(fixture);

      expect(service.executeQuery).toHaveBeenCalled();
      expect(service.executeQuery.mock.calls.at(-1)![1].deleted).toBe('only');
      const link: HTMLAnchorElement | null = fixture.nativeElement.querySelector('a[href]');
      expect(link?.getAttribute('href')).toContain('deleted=only');
      expect(c.permissions()?.canRead).toBe(true);
    });

    it('a click anywhere on a recycle-bin row opens it, with ?deleted and the list as the return URL', async () => {
      const { fixture, c } = await setup({}, { deleted: 'only' });
      const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
      const row = { id: 'people/1', values: [] } as QueryResultItem;
      const cell = document.createElement('td');

      (c as any).onRowClick({ row, rowIndex: 0, rowKey: 'people/1', originalEvent: new MouseEvent('click') });
      expect(navigate).toHaveBeenCalledWith(['/po', c.entityType()?.alias ?? c.entityType()?.id, 'people/1'], expect.objectContaining({
        queryParams: { deleted: 'only' },
        state: { sparkReturnUrl: TestBed.inject(Router).url },
      }));

      // A click on a control inside the row (a vote button, the link itself) is the control's.
      navigate.mockClear();
      const button = document.createElement('button');
      cell.appendChild(button);
      (c as any).onRowClick({ row, rowIndex: 0, rowKey: 'people/1', originalEvent: { target: button } });
      expect(navigate).not.toHaveBeenCalled();
      fixture.destroy();
    });

    it('treats exclude as the default', async () => {
      const { service } = await setup({}, { deleted: 'exclude' });
      expect('deleted' in service.executeQuery.mock.calls[0][1]).toBe(false);
    });
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

  describe('ship-vs-draw columns', () => {
    it('does not draw a column marked isVisible false, but keeps its value reachable', async () => {
      // A renderer needing a sibling value (a lock glyph beside a name) reads it off the row; the
      // grid must not give it a column. showedOn decides the wire, isVisible decides the drawing.
      const cols = [
        { name: 'Name', dataType: 'string', order: 1, isVisible: true },
        { name: 'IsPrivate', dataType: 'boolean', order: 2, isVisible: false },
      ] as any;
      const { c } = await setup({}, {
        data: [{ id: 'r/1', values: [{ key: 'Name', value: 'spark' }, { key: 'IsPrivate', value: true }] }],
        columns: cols,
      });

      expect(c.visibleColumns().map((x: any) => x.name)).toEqual(['Name']);
      expect(c.allColumns().map((x: any) => x.name)).toEqual(['Name', 'IsPrivate']);
    });

    it('treats an absent isVisible as visible', async () => {
      // A server predating the field must keep drawing everything.
      const cols = [{ name: 'Name', dataType: 'string', order: 1 }] as any;
      const { c } = await setup({}, { data: [], columns: cols });

      expect(c.visibleColumns().map((x: any) => x.name)).toEqual(['Name']);
    });
  });

  describe('composed rows have no default detail link (R10)', () => {
    it('withholds the row link when the type has no clrType', async () => {
      // Live in DemoApp before this: Read/StartPage implies Query/StartPage, so a composed grid
      // rendered every row linked to /po/{type}/{rowId} — which does not 404, it silently
      // re-renders the same composed page. A plausible wrong page is worse than a missing one.
      const composed = { id: 't-1', name: 'StartPage', alias: 'startpage', attributes: [] } as any;
      const { c } = await setup({ getEntityTypes: vi.fn().mockResolvedValue([composed]) });
      c.entityType.set(composed);

      expect((c as any).rowRouteFor({ id: 'collections/people', values: [] })).toBeNull();
    });

    it('still links an entity-backed row', async () => {
      const { c } = await setup();

      expect((c as any).rowRouteFor({ id: 'people/1', values: [] })).not.toBeNull();
    });

    it('lets a host override the withheld link for composed rows that do have a page', async () => {
      const composed = { id: 't-1', name: 'StartPage', alias: 'startpage', attributes: [] } as any;
      const { c } = await setup(
        { getEntityTypes: vi.fn().mockResolvedValue([composed]) },
        { rowRoute: (r: QueryResultItem) => ['/a', r.id] });
      c.entityType.set(composed);

      expect((c as any).rowRouteFor({ id: 'accounts/spark', values: [] })).toEqual(['/a', 'accounts/spark']);
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

  describe('attribute descriptions (#348)', () => {
    it('carries the description of a column through to the header template, and nowhere else', async () => {
      // The header cells are drawn by the Lit-based datatable (mp-datatable), which does not
      // upgrade under jsdom, so the [i] itself is not observable here — the form, detail and
      // component specs pin the rendering; this pins that the grid hands the datatable the
      // description on the column it reads in its header template.
      //
      // This gap is why a real bug shipped: until ng-bootstrap 22.18.0 the datatable mounted this
      // header template inside its shadow root, where the [i]'s three stylesheets (global
      // Bootstrap, spark-icon's SVG sizing, its own component styles) all live in document.head
      // and none arrived — so the [i] rendered unstyled in every grid while every spec passed.
      // Nothing under jsdom can catch that; it needs a real browser. See
      // docs/ngbootstrap_lightdom_upgrade_PRD.md.
      const page = {
        ...samplePage,
        columns: [
          { name: 'FirstName', dataType: 'string', order: 1, description: { en: 'Given name.' } } as any,
          { name: 'LastName', dataType: 'string', order: 2 } as any,
        ],
      };
      const { c } = await setup({ executeQuery: vi.fn().mockResolvedValue(page) });

      expect(c.visibleColumns().map(col => col.description?.['en'])).toEqual(['Given name.', undefined]);
    });
  });

  describe('column filters (#431)', () => {
    const filterPage = {
      columns: [
        { name: 'FirstName', dataType: 'string', order: 1 } as any,
        { name: 'Secret', dataType: 'string', order: 2, canFilter: false, canSort: false } as any,
      ],
      items: [],
      totalItems: 0,
    };

    it('carries the resolved capability flags through to the columns it draws', async () => {
      const { c } = await setup({ executeQuery: vi.fn().mockResolvedValue(filterPage) });

      // Asserted on component state, not on the header cells: mp-datatable is a Lit element and
      // does not upgrade under jsdom, so nothing it renders is observable here.
      expect(c.visibleColumns().map((col: any) => col.canFilter)).toEqual([undefined, false]);
      expect(c.visibleColumns().map((col: any) => col.canSort)).toEqual([undefined, false]);
    });

    it('translates a values change into includes and refetches from page one', async () => {
      const executeQuery = vi.fn().mockResolvedValue(filterPage);
      const { c, fixture } = await setup({ executeQuery });

      const before = c.fetchFn();
      c.settings.set(new DatatableSettings({
        perPage: { values: [10, 25, 50], selected: 10 },
        page: { values: [1, 2, 3], selected: 3 },
        sortColumns: [],
      }));

      c['onFilterChange']({
        mode: 'values',
        column: 'FirstName',
        selected: [{ value: 'Alice', label: 'Alice' }],
        inverse: false,
      } as any);
      await settle(fixture);

      // Page 1, because the old page number means nothing against a different result set.
      expect(c.settings().page.selected).toBe(1);
      // A NEW fetch identity. The datatable dedupes reloads by (page, perPage, sort) — none of
      // which a filter changes — so without this the request is silently never made.
      expect(c.fetchFn()).not.toBe(before);
    });

    it('sends excludes rather than a flag when the selection is inversed', async () => {
      const executeQuery = vi.fn().mockResolvedValue(filterPage);
      const { c, fixture } = await setup({ executeQuery });

      c['onFilterChange']({
        mode: 'values',
        column: 'FirstName',
        selected: [{ value: 'Alice', label: 'Alice' }],
        inverse: true,
      } as any);
      await settle(fixture);

      await c.fetchFn()!({ page: 1, perPage: 10, sortColumns: [] } as any);

      const body = executeQuery.mock.calls.at(-1)![1];
      expect(body.columns).toEqual([{ name: 'FirstName', excludes: ['Alice'] }]);
    });

    it('drops a column from the filter set when its selection is emptied', async () => {
      const executeQuery = vi.fn().mockResolvedValue(filterPage);
      const { c, fixture } = await setup({ executeQuery });

      c['onFilterChange']({ mode: 'values', column: 'FirstName', selected: [{ value: 'Alice', label: 'Alice' }], inverse: false } as any);
      c['onFilterChange']({ mode: 'values', column: 'FirstName', selected: [], inverse: false } as any);
      await settle(fixture);

      await c.fetchFn()!({ page: 1, perPage: 10, sortColumns: [] } as any);

      // An empty selection is the absent filter, not a filter matching nothing.
      const body = executeQuery.mock.calls.at(-1)![1];
      expect(body.columns).toEqual([]);
    });

    it('asks the server only for the OTHER columns\' filters when listing values', async () => {
      const getDistinctValues = vi.fn().mockResolvedValue({ matching: [], remaining: [], hasMore: false });
      const { c, fixture } = await setup({
        executeQuery: vi.fn().mockResolvedValue(filterPage),
        getDistinctValues,
      });

      c['onFilterChange']({ mode: 'values', column: 'FirstName', selected: [{ value: 'Alice', label: 'Alice' }], inverse: false } as any);
      await settle(fixture);

      // A stable function, not a signal: it reads the filters when CALLED, which is what removes the
      // reassign-and-abort race that a per-change identity introduced.
      await c['distinctsFn']({ column: 'FirstName', search: '', signal: new AbortController().signal });

      // Its own filter is excluded: a panel must offer the values you could still pick, not only
      // the ones you already picked.
      expect(getDistinctValues).toHaveBeenCalledWith('q-all', 'FirstName', expect.objectContaining({ columns: [] }));
    });

    it('lists values under a deleted parent with parentDeleted: include, and only under a parent (#460)', async () => {
      const getDistinctValues = vi.fn().mockResolvedValue({ matching: [], remaining: [], hasMore: false });
      const request = { column: 'FirstName', search: '', signal: new AbortController().signal };

      const under = await setup(
        { executeQuery: vi.fn().mockResolvedValue(filterPage), getDistinctValues },
        { parentId: 'companies/1', parentType: 'Company', parentDeleted: true });
      await under.c['distinctsFn'](request);
      expect(getDistinctValues.mock.calls.at(-1)![2]).toEqual(expect.objectContaining({
        parentId: 'companies/1', parentType: 'Company', parentDeleted: 'include',
      }));

      TestBed.resetTestingModule();
      const top = await setup(
        { executeQuery: vi.fn().mockResolvedValue(filterPage), getDistinctValues },
        { parentDeleted: true });
      await top.c['distinctsFn'](request);
      expect(getDistinctValues.mock.calls.at(-1)![2].parentDeleted).toBeUndefined();
    });

    describe('selecting < none >', () => {
      // `Col == null` misses a field the document never wrote, so a selection containing the null
      // entry is sent as the complement: an exclusion of every real value NOT selected.
      const complete = { matching: [{ value: 'Alice' }, { value: 'Bob' }, { value: 'Carol' }, { value: null }], remaining: [], hasMore: false };

      async function filtered(distincts: any, search = '') {
        const executeQuery = vi.fn().mockResolvedValue(filterPage);
        const s = await setup({ executeQuery, getDistinctValues: vi.fn().mockResolvedValue(distincts) });
        // Protected members; reached directly, as the rest of this block does.
        const grid = s.c as any;
        await grid.distinctsFn({ column: 'FirstName', search, signal: new AbortController().signal });
        const apply = async (values: unknown[], inverse = false) => {
          grid.onFilterChange({ mode: 'values', column: 'FirstName', selected: values.map(v => ({ value: v, label: String(v) })), inverse } as any);
          await settle(s.fixture);
          await s.c.fetchFn()!({ page: 1, perPage: 10, sortColumns: [] } as any);
          return executeQuery.mock.calls.at(-1)![1].columns;
        };
        return { ...s, apply };
      }

      it('sends the unselected real values as excludes when the list is complete', async () => {
        const { apply } = await filtered(complete);

        expect(await apply([null, 'Alice'])).toEqual([{ name: 'FirstName', excludes: ['Bob', 'Carol'] }]);
      });

      it('drops the filter when everything, none included, is selected', async () => {
        const { apply } = await filtered(complete);

        expect(await apply([null, 'Alice', 'Bob', 'Carol'])).toEqual([]);
      });

      it('cannot build a complement from a truncated list, so sends the selection as-is', async () => {
        const { apply } = await filtered({ ...complete, hasMore: true });

        expect(await apply([null, 'Alice'])).toEqual([{ name: 'FirstName', includes: [null, 'Alice'] }]);
      });

      it('never treats a searched (subset) list as complete', async () => {
        const { apply } = await filtered(complete, 'a');

        expect(await apply([null])).toEqual([{ name: 'FirstName', includes: [null] }]);
      });

      it('an inversed selection containing none stays a plain exclusion', async () => {
        const { apply } = await filtered(complete);

        expect(await apply([null], true)).toEqual([{ name: 'FirstName', excludes: [null] }]);
      });
    });

    it('ignores a comparison-mode change, which the panel never declares', async () => {
      const { c } = await setup({ executeQuery: vi.fn().mockResolvedValue(filterPage) });
      const before = c.fetchFn();

      (c as any).onFilterChange({ mode: 'comparison', column: 'FirstName' });

      expect(c.fetchFn()).toBe(before);
    });

    it('lists no values before the query is known', async () => {
      const getDistinctValues = vi.fn();
      const { c } = await setup({ getQuery: vi.fn().mockRejectedValue({ status: 404 }), getDistinctValues });

      expect(await (c as any).distinctsFn({ column: 'FirstName', search: '', signal: new AbortController().signal })).toBeNull();
      expect(getDistinctValues).not.toHaveBeenCalled();
    });
  });

  describe('custom actions', () => {
    const archive = { name: 'Archive', displayName: { en: 'Archive' }, showedOn: 'query', refreshOnCompleted: false } as any;
    const other = { ...archive, name: 'Other' };

    it('withholds the actions the result disabled, case-insensitively, re-read per page', async () => {
      const executeQuery = vi.fn().mockResolvedValue({ ...samplePage, disabledActions: ['ARCHIVE'] });
      const { c } = await setup({ executeQuery, getCustomActions: vi.fn().mockResolvedValue([archive, other]) });

      expect(c.visibleCustomActions().map(a => a.name)).toEqual(['Other']);

      executeQuery.mockResolvedValue(samplePage);
      await c.fetchFn()!({ page: 2, perPage: 10, sortColumns: [] } as any);
      expect(c.visibleCustomActions().map(a => a.name)).toEqual(['Archive', 'Other']);
    });

    it('stops offering New when the result withholds it, leaving canCreate the bare right (#460, D13)', async () => {
      const executeQuery = vi.fn().mockResolvedValue({ ...samplePage, disabledActions: ['new'] });
      const { c } = await setup({ executeQuery });

      expect(c.canCreate()).toBe(true);
      expect(c.offersCreate()).toBe(false);

      executeQuery.mockResolvedValue(samplePage);
      await c.fetchFn()!({ page: 2, perPage: 10, sortColumns: [] } as any);
      expect(c.offersCreate()).toBe(true);
    });

    it('asks for confirmation and does nothing when it is declined', async () => {
      const confirmSpy = vi.spyOn(globalThis, 'confirm').mockReturnValue(false);
      try {
        const { c, service } = await setup({ getCustomActions: vi.fn().mockResolvedValue([archive]) });

        await c.onCustomAction({ ...archive, confirmationMessageKey: 'confirm.archive' });

        expect(confirmSpy).toHaveBeenCalledWith('confirm.archive');
        expect(service.executeCustomAction).not.toHaveBeenCalled();
      } finally {
        confirmSpy.mockRestore();
      }
    });

    it('runs a confirmed action, emits, and re-runs the query when asked to refresh', async () => {
      const confirmSpy = vi.spyOn(globalThis, 'confirm').mockReturnValue(true);
      try {
        const { c, service } = await setup({ getCustomActions: vi.fn().mockResolvedValue([archive]) });
        const executed = vi.fn();
        c.customActionExecuted.subscribe(executed);
        const before = c.fetchFn();

        await c.onCustomAction({ ...archive, confirmationMessageKey: 'confirm.archive', refreshOnCompleted: true });

        expect(service.executeCustomAction).toHaveBeenCalledTimes(1);
        expect(executed).toHaveBeenCalledTimes(1);
        // A new fetch identity is what makes the datatable re-request the current page.
        expect(c.fetchFn()).not.toBe(before);
      } finally {
        confirmSpy.mockRestore();
      }
    });

    it('keeps the fetch when the action does not ask for a refresh', async () => {
      const { c } = await setup({ getCustomActions: vi.fn().mockResolvedValue([archive]) });
      const before = c.fetchFn();

      await c.onCustomAction(archive);

      expect(c.fetchFn()).toBe(before);
    });

    it('falls back to the translated generic text for a failure with no message', async () => {
      const { c } = await setup({ executeCustomAction: vi.fn().mockRejectedValue({}) });

      await c.onCustomAction(archive);

      expect(c.errorMessage()).toBe('common.actionFailed');
    });
  });

  describe('error text', () => {
    it('shows the server-sent error for a non-404 load failure', async () => {
      const { c } = await setup({ getQuery: vi.fn().mockRejectedValue({ status: 500, error: { error: 'Index is stale' } }) });

      expect(c.errorMessage()).toBe('Index is stale');
    });

    it('falls back to the error message and then to a generic text', async () => {
      const first = await setup({ getQuery: vi.fn().mockRejectedValue({ status: 0, message: 'Http failure' }) });
      expect(first.c.errorMessage()).toBe('Http failure');

      TestBed.resetTestingModule();
      const second = await setup({ getQuery: vi.fn().mockRejectedValue({ status: 500 }) });
      expect(second.c.errorMessage()).toBe('common.unexpectedError');
    });

    it('a failed page fetch reports and emits, then renders an empty page rather than throwing', async () => {
      const { c, service } = await setup();
      const emitted = vi.fn();
      c.error.subscribe(emitted);
      service.executeQuery.mockRejectedValue({ status: 500, error: { error: 'Timeout' } });

      const page = await c.fetchFn()!({ page: 3, perPage: 25, sortColumns: [] } as any);

      expect(page).toEqual({ data: [], totalRecords: 0, totalPages: 1, perPage: 25, page: 3 });
      expect(c.errorMessage()).toBe('Timeout');
      expect(c.resultCount()).toBe(0);
      expect(emitted).toHaveBeenCalledWith(expect.objectContaining({ status: 500 }));
    });

    it('a later successful page clears the error', async () => {
      const { c, service } = await setup();
      service.executeQuery.mockRejectedValueOnce({ status: 500, error: { error: 'Timeout' } });
      await c.fetchFn()!({ page: 1, perPage: 10, sortColumns: [] } as any);

      const page = await c.fetchFn()!({ page: 1, perPage: 10, sortColumns: [] } as any);

      expect(c.errorMessage()).toBeNull();
      expect(page.totalPages).toBe(1);
      expect(page.data).toHaveLength(1);
    });
  });

  describe('entity type from the query source', () => {
    // A Database.* query need not declare entityType; the grid maps its source onto a type name.
    const typeNamed = (name: string, clrType = `Test.${name}`) =>
      ({ id: `t-${name}`, name, alias: name.toLowerCase(), clrType, attributes: [] }) as any;

    it.each([
      ['Database.Categories', 'Category'],
      ['Database.Boxes', 'Box'],
      ['Database.Children', 'Child'],
      ['Database.Cars', 'Car'],
      ['Staff', 'Staff'],
    ])('maps %s to %s', async (source, expected) => {
      const { c } = await setup({
        getQuery: vi.fn().mockResolvedValue({ ...allPeopleQuery, entityType: undefined, source }),
        getEntityTypes: vi.fn().mockResolvedValue([personType, typeNamed(expected)]),
      });

      expect(c.entityType()?.name).toBe(expected);
    });

    it('falls back to the CLR type name when no type is named after the source', async () => {
      const { c } = await setup({
        getQuery: vi.fn().mockResolvedValue({ ...allPeopleQuery, entityType: undefined, source: 'Database.Autos' }),
        getEntityTypes: vi.fn().mockResolvedValue([personType, typeNamed('Car', 'Fleet.Auto')]),
      });

      expect(c.entityType()?.name).toBe('Car');
    });

    it('warns, and asks for no actions, when the query names a type that resolves to nothing (M15)', async () => {
      const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
      try {
        const { c, service } = await setup({
          getQuery: vi.fn().mockResolvedValue({ ...allPeopleQuery, name: 'GhostQuery', entityType: 'Ghost' }),
        });

        expect(c.entityType()).toBeFalsy();
        expect(service.getCustomActions).not.toHaveBeenCalled();
        const messages = warn.mock.calls.map(args => String(args[0]));
        expect(messages.some(m => m.includes(`Query 'GhostQuery'`) && m.includes(`entity type 'Ghost'`)
          && m.includes('offers no actions'))).toBe(true);
      } finally {
        warn.mockRestore();
      }
    });

    it('does not warn when the entity type resolves', async () => {
      const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
      try {
        await setup();

        expect(warn.mock.calls.some(args => String(args[0]).includes('resolves to no type'))).toBe(false);
      } finally {
        warn.mockRestore();
      }
    });

    it.each([
      ['Database.Vehicles', 'Vehicle'],
      ['Database.Roles', 'Role'],
    ])('maps %s to %s although the -es rule mis-singularizes it', async (source, expected) => {
      // `singularize` strips "es" from anything ending in it, so Vehicles becomes "Vehicl" and Roles
      // "Rol". The page resolver (spark-query-list) matches `name + 's'` and found these types; the
      // grid did not, and resolved no entity type at all — no permissions, no actions, no row links.
      const { c } = await setup({
        getQuery: vi.fn().mockResolvedValue({ ...allPeopleQuery, entityType: undefined, source }),
        getEntityTypes: vi.fn().mockResolvedValue([personType, typeNamed(expected)]),
      });

      expect(c.entityType()?.name).toBe(expected);
    });

    it('matches a declared entityType by alias, case-insensitively', async () => {
      const { c } = await setup({ getQuery: vi.fn().mockResolvedValue({ ...allPeopleQuery, entityType: 'PERSON' }) });

      expect(c.entityType()?.id).toBe('t-person');
    });
  });

  describe('renderer registry', () => {
    @Component({ selector: 'spec-stars-column', standalone: true, template: '' })
    class StarsColumn {}
    const starsLabel = (v: unknown) => `${v} stars`;
    const registry = [
      { name: 'stars', columnComponent: StarsColumn, filterLabel: starsLabel },
      { name: 'edit-only', editComponent: StarsColumn },
    ];

    it('resolves the column component and filter label of a registered renderer', async () => {
      const { c } = await setup({}, {}, registry);
      const col = { name: 'Rating', dataType: 'number', renderer: 'stars' } as any;

      expect(c.getColumnRendererComponent(col)).toBe(StarsColumn);
      expect(c.filterLabelFor(col)).toBe(starsLabel);
    });

    it('is null for a plain column, an unknown renderer, and a renderer without those slots', async () => {
      const { c } = await setup({}, {}, registry);

      for (const renderer of [undefined, 'nope', 'edit-only']) {
        const col = { name: 'Rating', dataType: 'number', renderer } as any;
        expect(c.getColumnRendererComponent(col)).toBeNull();
        expect(c.filterLabelFor(col)).toBeNull();
      }
    });
  });

});
