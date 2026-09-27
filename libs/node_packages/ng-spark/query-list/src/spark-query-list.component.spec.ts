import { TestBed, ComponentFixture } from '@angular/core/testing';
import { provideRouter, Router, Routes } from '@angular/router';
import { DatatableSettings } from '@mintplayer/ng-bootstrap/datatable';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { Subject } from 'rxjs';

import { SparkQueryListComponent } from './spark-query-list.component';
import { SparkService, SparkStreamingService, SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SPARK_ATTRIBUTE_RENDERERS } from '@mintplayer/ng-spark/renderers';
import { EntityType, ShowedOn, SparkQuery } from '@mintplayer/ng-spark/models';
import { StubComponent, nextNavigationEnd, settle } from '../../src/test-utils';

/**
 * What remains page-shaped after the grid moved out: route resolution, and streaming.
 *
 * Columns, cells, paging, permissions and the row link are the grid's, and are asserted in
 * `spark-query-grid.component.spec.ts`. Duplicating them here would recreate exactly the
 * two-copies-that-drift problem the components were merged to end.
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

const routes: Routes = [
  { path: 'query/:queryId', component: SparkQueryListComponent },
  { path: 'po/:type', component: SparkQueryListComponent },
  { path: 'po/:type/new', component: StubComponent },
];

const langStub = { t: (k: string) => k, resolve: (v: any) => (typeof v === 'string' ? v : v?.en ?? '') };

async function setup(serviceOverrides: Record<string, unknown> = {}) {
  const service: any = {
    getEntityTypes: vi.fn().mockResolvedValue([personType]),
    getQueries: vi.fn().mockResolvedValue([allPeopleQuery]),
    getQuery: vi.fn().mockResolvedValue(allPeopleQuery),
    getPermissions: vi.fn().mockResolvedValue({ canQuery: true, canRead: true, canCreate: true, canEdit: true, canDelete: true }),
    getCustomActions: vi.fn().mockResolvedValue([]),
    executeQuery: vi.fn().mockResolvedValue({ columns: [], items: [], totalItems: 0, skip: 0, take: 50 }),
    executeCustomAction: vi.fn().mockResolvedValue(undefined),
    getLookupReference: vi.fn().mockResolvedValue({ values: [] }),
    ...serviceOverrides,
  };
  const streamSubject = new Subject<any>();
  const streaming: any = { connectToStreamingQuery: vi.fn(() => streamSubject.asObservable()) };
  TestBed.configureTestingModule({
    providers: [
      provideNoopAnimations(),
      provideRouter(routes),
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: SparkService, useValue: service },
      { provide: SparkStreamingService, useValue: streaming },
      { provide: SparkLanguageService, useValue: langStub },
      { provide: SPARK_ATTRIBUTE_RENDERERS, useValue: [] },
    ],
  });
  const harness = await RouterTestingHarness.create();
  return { harness, service, streaming, streamSubject };
}

async function navigate(harness: RouterTestingHarness, url: string) {
  const c = await harness.navigateByUrl(url, SparkQueryListComponent);
  await settle(harness.fixture, { rounds: 6 });
  return c as any;
}

describe('SparkQueryListComponent', () => {
  beforeEach(() => TestBed.resetTestingModule());

  describe('route resolution', () => {
    it('takes the query id straight from query/:queryId', async () => {
      const { harness } = await setup();
      const c = await navigate(harness, '/query/q-all');

      expect(c.queryId()).toBe('q-all');
    });

    /**
     * `po/:type` names an entity type, not a query, and the translation between them is the only
     * reason this component still exists rather than the route pointing at the grid.
     */
    it('resolves po/:type to the query that lists that type', async () => {
      const { harness, service } = await setup();
      const c = await navigate(harness, '/po/person');

      expect(service.getQueries).toHaveBeenCalled();
      expect(c.queryId()).toBe('allpeople');
    });

    it('reports a type that resolves to no query, rather than spinning', async () => {
      const { harness } = await setup({ getQueries: vi.fn().mockResolvedValue([]) });
      const c = await navigate(harness, '/po/person');

      expect(c.queryId()).toBeNull();
      expect(c.errorMessage()).toBeTruthy();
    });

    /**
     * A denied query answers 404 with the same body as a missing one (audit M-3), so the message
     * must not claim to know which it was.
     */
    it('reports an unknown type without disclosing whether it exists', async () => {
      const { harness } = await setup({ getEntityTypes: vi.fn().mockResolvedValue([]) });
      const c = await navigate(harness, '/po/nope');

      expect(c.errorMessage()).toBe('spark.query.unavailable');
    });

    it('matches a query by its source name when it declares no entityType, singular or plural', async () => {
      const bySource = { ...allPeopleQuery, alias: undefined, id: 'q-src', entityType: undefined, source: 'Database.Persons' } as any;
      const { harness } = await setup({ getQueries: vi.fn().mockResolvedValue([bySource]) });
      const c = await navigate(harness, '/po/person');

      // No alias, so the id is what the grid is handed.
      expect(c.queryId()).toBe('q-src');
    });

    it('matches an undotted source name too', async () => {
      const bare = { ...allPeopleQuery, alias: 'bare', entityType: undefined, source: 'Person' } as any;
      const other = { ...allPeopleQuery, alias: 'other', entityType: 'Car', source: 'Database.Cars' } as any;
      const { harness } = await setup({ getQueries: vi.fn().mockResolvedValue([other, bare]) });
      const c = await navigate(harness, '/po/person');

      expect(c.queryId()).toBe('bare');
    });

    /**
     * The handler is async inside a subscribe, so a rejection used to land nowhere and the page
     * spun forever. It must surface as the page's own error instead.
     */
    it('renders a metadata load failure instead of spinning', async () => {
      const { harness } = await setup({
        getEntityTypes: vi.fn().mockRejectedValue({ status: 500, error: { error: 'Database offline' } }),
      });
      const c = await navigate(harness, '/po/person');

      expect(c.errorMessage()).toBe('Database offline');
      expect(harness.fixture.nativeElement.querySelector('bs-alert')?.textContent).toContain('Database offline');
    });

    it('falls back to the error message, then to a generic text, for a failure without a body', async () => {
      const first = await setup({ getQueries: vi.fn().mockRejectedValue(new Error('socket hang up')) });
      expect((await navigate(first.harness, '/po/person')).errorMessage()).toBe('socket hang up');

      TestBed.resetTestingModule();
      const second = await setup({ getQueries: vi.fn().mockRejectedValue({}) });
      expect((await navigate(second.harness, '/po/person')).errorMessage()).toBe('An unexpected error occurred');
    });
  });

  describe('streaming', () => {
    const streamingQuery = { ...allPeopleQuery, id: 'q-live', isStreamingQuery: true } as any;

    async function live() {
      const s = await setup({ getQuery: vi.fn().mockResolvedValue(streamingQuery) });
      const c = await navigate(s.harness, '/query/q-live');
      return { ...s, c };
    }

    it('connects when the grid resolves a streaming query', async () => {
      const { c, streaming } = await live();

      expect(streaming.connectToStreamingQuery).toHaveBeenCalledWith('q-live');
      expect(c.isStreaming()).toBe(true);
    });

    /**
     * The grid must not also fetch. It recognises a streaming query itself for this reason —
     * waiting to be handed `data` would still have fired one /execute before the socket connected.
     */
    it('does not also execute the query over http', async () => {
      const { service } = await live();

      expect(service.executeQuery).not.toHaveBeenCalled();
    });

    it('feeds the snapshot to the grid and applies a patch in place', async () => {
      const { c, harness, streamSubject } = await live();

      streamSubject.next({
        type: 'snapshot',
        columns: [{ name: 'FirstName', dataType: 'string', order: 1 }],
        data: [
          { id: 'people/1', breadcrumb: 'Alice', values: [{ key: 'FirstName', value: 'Alice' }] },
          { id: 'people/2', breadcrumb: 'Bob', values: [{ key: 'FirstName', value: 'Bob' }] },
        ],
      });
      await settle(harness.fixture, { rounds: 6 });
      expect(c.gridData()).toHaveLength(2);

      streamSubject.next({ type: 'patch', updated: [{ id: 'people/1', values: { FirstName: 'Alicia' } }] });
      await settle(harness.fixture, { rounds: 6 });

      const alice = c.gridData()!.find((i: any) => i.id === 'people/1')!;
      expect(alice.values.find((v: any) => v.key === 'FirstName')?.value).toBe('Alicia');
    });

    it('filters the snapshot client-side — there is no request to attach a search to', async () => {
      const { c, harness, streamSubject } = await live();
      streamSubject.next({
        type: 'snapshot',
        columns: [{ name: 'FirstName', dataType: 'string', order: 1 }],
        data: [
          { id: 'people/1', values: [{ key: 'FirstName', value: 'Alice' }] },
          { id: 'people/2', values: [{ key: 'FirstName', value: 'Bob' }] },
        ],
      });
      await settle(harness.fixture, { rounds: 6 });

      c.searchTerm.set('bob');
      await settle(harness.fixture, { rounds: 6 });

      expect(c.gridData()).toHaveLength(1);
      expect(c.gridData()![0].id).toBe('people/2');
    });

    it('surfaces a stream error', async () => {
      const { c, harness, streamSubject } = await live();

      streamSubject.error(new Error('socket died'));
      await settle(harness.fixture, { rounds: 6 });

      expect(c.isStreaming()).toBe(false);
      expect(c.errorMessage()).toContain('socket died');
    });

    it('renders an error message sent over the stream as the page error, and stops being live', async () => {
      // The server sends `error` only on its way to closing the socket (StreamExecuteQuery's
      // SendErrorAndCloseAsync), so it is terminal: the alert replaces the grid, and with the grid
      // gone the stream is disconnected rather than left showing LIVE over nothing.
      const { c, harness, streamSubject } = await live();

      streamSubject.next({ type: 'error', message: 'Access denied' });
      await settle(harness.fixture, { rounds: 6 });

      expect(c.errorMessage()).toBe('Access denied');
      expect(harness.fixture.nativeElement.querySelector('bs-alert')?.textContent).toContain('Access denied');
      expect(harness.fixture.nativeElement.querySelector('spark-query-grid')).toBeNull();
      expect(c.isStreaming()).toBe(false);
    });

    it('stops being live when the server completes the stream', async () => {
      const { c, harness, streamSubject } = await live();

      streamSubject.complete();
      await settle(harness.fixture, { rounds: 6 });

      expect(c.isStreaming()).toBe(false);
    });

    it('ignores a patch that updates nothing and leaves rows it does not name untouched', async () => {
      const { c, harness, streamSubject } = await live();
      streamSubject.next({
        type: 'snapshot',
        columns: [{ name: 'FirstName', dataType: 'string', order: 1 }],
        data: [
          { id: 'people/1', values: [{ key: 'FirstName', value: 'Alice' }] },
          { id: 'people/2', values: [{ key: 'FirstName', value: 'Bob' }, { key: 'Age', value: 30 }] },
        ],
      });
      await settle(harness.fixture, { rounds: 6 });
      const bob = c.gridData()!.find((i: any) => i.id === 'people/2');

      streamSubject.next({ type: 'patch', updated: [] });
      streamSubject.next({ type: 'patch', updated: [{ id: 'people/1', values: { Age: 41 } }] });
      await settle(harness.fixture, { rounds: 6 });

      // A key the patch does not carry keeps its value; the row the patch does not name keeps its identity.
      const alice = c.gridData()!.find((i: any) => i.id === 'people/1')!;
      expect(alice.values).toEqual([{ key: 'FirstName', value: 'Alice' }]);
      expect(c.gridData()!.find((i: any) => i.id === 'people/2')).toBe(bob);
    });

    it('re-applies the grid sort to the snapshot, descending and with a tie-breaker', async () => {
      // The sort must survive every patch: re-deriving from the snapshot without it would reorder the
      // grid under the user on each update.
      const { c, harness, streamSubject } = await live();
      streamSubject.next({
        type: 'snapshot',
        columns: [{ name: 'Last', dataType: 'string', order: 1 }, { name: 'First', dataType: 'string', order: 2 }],
        data: [
          { id: 'p/1', values: [{ key: 'Last', value: 'Smith' }, { key: 'First', value: 'Anna' }] },
          { id: 'p/2', values: [{ key: 'Last', value: 'Jones' }, { key: 'First', value: 'Carl' }] },
          { id: 'p/3', values: [{ key: 'Last', value: 'Smith' }, { key: 'First', value: 'Bea' }] },
          // No Last at all: sorts as the empty string, not as "undefined".
          { id: 'p/4', values: [{ key: 'First', value: 'Dan' }] },
        ],
      });
      await settle(harness.fixture, { rounds: 6 });

      const grid = c.grid();
      grid.settings.set(new DatatableSettings({
        perPage: { values: [50], selected: 50 },
        page: { values: [1], selected: 1 },
        sortColumns: [
          { property: 'Last', direction: 'descending' },
          { property: 'First', direction: 'ascending' },
        ],
      }));
      await settle(harness.fixture, { rounds: 6 });

      expect(c.gridData()!.map((i: any) => i.id)).toEqual(['p/1', 'p/3', 'p/2', 'p/4']);

      streamSubject.next({ type: 'patch', updated: [{ id: 'p/2', values: { Last: 'Zed' } }] });
      await settle(harness.fixture, { rounds: 6 });

      expect(c.gridData()!.map((i: any) => i.id)).toEqual(['p/2', 'p/1', 'p/3', 'p/4']);
    });

    it('hands the grid null for a non-streaming query, so it fetches for itself', async () => {
      const { harness, service } = await setup();
      const c = await navigate(harness, '/query/q-all');

      expect(c.gridData()).toBeNull();
      expect(service.executeQuery).toHaveBeenCalled();
    });
  });

  describe('page chrome', () => {
    it('reads canCreate from the grid so the New button reflects the real permission', async () => {
      const { harness } = await setup({
        getPermissions: vi.fn().mockResolvedValue({ canQuery: true, canRead: true, canCreate: false, canEdit: false, canDelete: false }),
      });
      const c = await navigate(harness, '/query/q-all');

      expect(c.canCreate()).toBe(false);
    });

    it('shows the caption from the resolved query', async () => {
      const { harness } = await setup({
        getQuery: vi.fn().mockResolvedValue({ ...allPeopleQuery, description: { en: 'Everyone' } }),
      });
      await navigate(harness, '/query/q-all');

      expect(harness.fixture.nativeElement.textContent).toContain('Everyone');
    });

    it('onCreate emits and opens the new-object page under the type alias', async () => {
      const { harness } = await setup();
      const c = await navigate(harness, '/query/q-all');
      const created = vi.fn();
      c.createClicked.subscribe(created);

      const navigated = nextNavigationEnd();
      c.onCreate();
      await navigated;

      expect(created).toHaveBeenCalledTimes(1);
      expect(TestBed.inject(Router).url).toBe('/po/person/new');
    });

    it('onCreate only emits while the entity type is still unknown', async () => {
      const { harness } = await setup({ getEntityTypes: vi.fn().mockResolvedValue([]) });
      const c = await navigate(harness, '/query/q-all');
      const created = vi.fn();
      c.createClicked.subscribe(created);
      const navigate$ = vi.spyOn(TestBed.inject(Router), 'navigate');

      c.onCreate();

      expect(created).toHaveBeenCalledTimes(1);
      expect(navigate$).not.toHaveBeenCalled();
    });

    it('clearSearch empties the term and the grid refetches without it', async () => {
      const { harness, service } = await setup();
      const c = await navigate(harness, '/query/q-all');
      c.searchTerm.set('ali');
      await settle(harness.fixture, { rounds: 6 });

      c.clearSearch();
      await settle(harness.fixture, { rounds: 6 });

      expect(c.searchTerm()).toBe('');
      expect(c.grid().search()).toBe('');
      expect(service.executeQuery.mock.calls.at(-1)![1].search).toBeUndefined();
    });

    it('delegates custom actions and their enabled state to the grid, which holds the selection', async () => {
      const action = { name: 'Archive', displayName: { en: 'Archive' }, showedOn: 'query', selectionRule: '=1', offset: 0 } as any;
      const { harness, service } = await setup({ getCustomActions: vi.fn().mockResolvedValue([action]) });
      const c = await navigate(harness, '/query/q-all');

      expect(c.customActions().map((a: any) => a.name)).toEqual(['Archive']);
      expect(c.isActionEnabled(action)).toBe(false);
      c.grid().selection.set([{ id: 'people/1', values: [] }]);
      expect(c.isActionEnabled(action)).toBe(true);

      await c.onCustomAction(action);

      expect(service.executeCustomAction).toHaveBeenCalledWith('t-person', 'Archive', undefined, ['people/1'], undefined, 'q-all');
    });

    it('reports actions as disabled before the grid exists', async () => {
      const { harness } = await setup({ getEntityTypes: vi.fn().mockResolvedValue([]) });
      const c = await navigate(harness, '/po/nope');

      expect(c.isActionEnabled({ name: 'X' } as any)).toBe(false);
    });

    it('customActionClass allow-lists the server variant and defaults everything else', async () => {
      const { harness } = await setup();
      const c = await navigate(harness, '/query/q-all');

      expect(c.customActionClass({ variant: 'Danger' } as any)).toBe('btn btn-danger');
      expect(c.customActionClass({ variant: 'success' } as any)).toBe('btn btn-success');
      // Arriving from JSON: an arbitrary value must not reach the class attribute.
      expect(c.customActionClass({ variant: 'danger evil-class' } as any)).toBe('btn btn-outline-primary');
      expect(c.customActionClass({} as any)).toBe('btn btn-outline-primary');
    });
  });
});
