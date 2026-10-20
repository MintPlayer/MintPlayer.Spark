import { TestBed } from '@angular/core/testing';
import { provideRouter, Router, Routes } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { Subject } from 'rxjs';

import { SparkQueryListComponent } from './spark-query-list.component';
import { SparkService, SparkLiveService, SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SPARK_ATTRIBUTE_RENDERERS } from '@mintplayer/ng-spark/renderers';
import { EntityType, ShowedOn, SparkQuery, translated, resolveTranslation } from '@mintplayer/ng-spark/models';
import { StubComponent, nextNavigationEnd, settle } from '../../src/test-utils';

/**
 * What remains page-shaped after the grid moved out: route resolution and the chrome (LIVE badge included).
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
      isReadOnly: false, isRequired: false,
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
} as any;

const routes: Routes = [
  { path: 'query/:queryId', component: SparkQueryListComponent },
  { path: 'po/:type', component: SparkQueryListComponent },
  { path: 'po/:type/new', component: StubComponent },
];

const langStub = { t: (k: string) => k, resolve: (v: any) => (typeof v === 'string' ? v : resolveTranslation(v, 'en')) };

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
  const liveEvents = new Subject<any>();
  const live: any = { watchQuery: vi.fn(() => liveEvents.asObservable()) };
  TestBed.configureTestingModule({
    providers: [
      provideNoopAnimations(),
      provideRouter(routes),
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: SparkService, useValue: service },
      { provide: SparkLiveService, useValue: live },
      { provide: SparkLanguageService, useValue: langStub },
      { provide: SPARK_ATTRIBUTE_RENDERERS, useValue: [] },
    ],
  });
  const harness = await RouterTestingHarness.create();
  return { harness, service, live, liveEvents };
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

  /**
   * Live is the grid's now (X4): the page neither opens a socket nor filters or sorts rows itself. The
   * streaming branch it replaces held the whole set client-side and patched only the ids it already had,
   * so a row added on the server never appeared (defect 2) — the grid's own specs cover adding, removing
   * and reordering through server-side paging.
   */
  describe('live', () => {
    const liveQuery = { ...allPeopleQuery, id: 'q-live', live: true } as any;

    it('shows LIVE while the grid follows a live query, and never executes it over http', async () => {
      const { harness, service, live } = await setup({ getQuery: vi.fn().mockResolvedValue(liveQuery) });
      const c = await navigate(harness, '/query/q-live');

      expect(c.isLive()).toBe(true);
      expect(harness.fixture.nativeElement.querySelector('.spark-live-badge')).not.toBeNull();
      expect(live.watchQuery).toHaveBeenCalled();
      expect(service.executeQuery).not.toHaveBeenCalled();
    });

    it('a query that is not live is fetched over http and shows no LIVE badge', async () => {
      const { harness, service, live } = await setup();
      const c = await navigate(harness, '/query/q-all');

      expect(c.isLive()).toBe(false);
      expect(harness.fixture.nativeElement.querySelector('.spark-live-badge')).toBeNull();
      expect(service.executeQuery).toHaveBeenCalled();
      expect(live.watchQuery).not.toHaveBeenCalled();
    });

    it('drops LIVE when the subscription ends with an error, and shows the grid\'s message', async () => {
      const { harness, liveEvents } = await setup({ getQuery: vi.fn().mockResolvedValue(liveQuery) });
      const c = await navigate(harness, '/query/q-live');

      liveEvents.next({ type: 'error', code: 'unavailable', message: 'gone' });
      await settle(harness.fixture, { rounds: 6 });

      expect(c.isLive()).toBe(false);
      expect(c.gridError()).toBe('spark.query.unavailable');
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

    it('the recycle bin (?deleted=only) renders none of New, Delete or the custom actions (#460)', async () => {
      const actions = [
        { name: 'New', label: translated({ en: 'New' }), showedOn: 'both', offset: 0, isDefault: true },
        { name: 'Delete', label: translated({ en: 'Delete' }), showedOn: 'both', selectionRule: '>0', offset: 0, isDefault: true },
        { name: 'Archive', label: translated({ en: 'Archive' }), showedOn: 'query', selectionRule: '>0', offset: 0 },
      ];
      const shown = () => [...harness.fixture.nativeElement.querySelectorAll('.spark-actionbar [data-action]')]
        .map((b: Element) => b.getAttribute('data-action'));
      const { harness } = await setup({
        getCustomActions: vi.fn().mockResolvedValue(actions),
        getQuery: vi.fn().mockResolvedValue({ ...allPeopleQuery, selectionMode: 'multiple' }),
      });

      await navigate(harness, '/query/q-all');
      expect(new Set(shown())).toEqual(new Set(['New', 'Delete', 'Archive']));

      await navigate(harness, '/query/q-all?deleted=only');
      expect(shown()).toEqual([]);
    });

    it('shows the caption from the resolved query', async () => {
      const { harness } = await setup({
        getQuery: vi.fn().mockResolvedValue({ ...allPeopleQuery, label: translated({ en: 'Everyone' }) }),
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
      const action = { name: 'Archive', label: translated({ en: 'Archive' }), showedOn: 'query', selectionRule: '=1', offset: 0 } as any;
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

// The URL scope (contributions M5b): the History link opens `query/:id?parentId=…&parentType=…&Slot=…`.
describe('SparkQueryListComponent URL scope', () => {
  beforeEach(() => TestBed.resetTestingModule());

  it('executes the query with the parent and one column filter per known attribute, ignoring the rest', async () => {
    const { harness, service } = await setup();
    const c = await navigate(harness, '/query/q-all?parentId=Companies%2F1&parentType=Company&FirstName=Ann&FirstName=Bo&utm_source=mail&deleted=include');

    const options = service.executeQuery.mock.calls.at(-1)[1];
    expect(options.parentId).toBe('Companies/1');
    expect(options.parentType).toBe('Company');
    expect(options.columns).toEqual([{ name: 'FirstName', includes: ['Ann', 'Bo'] }]);
    expect(c.activeUrlFilters()).toEqual([{ name: 'FirstName', includes: ['Ann', 'Bo'] }]);
  });

  it('ignores a parent without its type', async () => {
    const { harness, service } = await setup();
    await navigate(harness, '/query/q-all?parentId=Companies%2F1');

    const options = service.executeQuery.mock.calls.at(-1)[1];
    expect(options.parentId).toBe('');
  });

  it('shows the URL filters as chips and clears one, or all with the parent', async () => {
    const { harness, service } = await setup();
    const c = await navigate(harness, '/query/q-all?parentId=Companies%2F1&parentType=Company&FirstName=Ann');
    const el = harness.routeNativeElement as HTMLElement;
    expect(el.querySelector('.spark-url-filter[data-filter="FirstName"]')?.textContent).toContain('Ann');

    c.clearUrlFilter('FirstName');
    await settle(harness.fixture, { rounds: 6 });
    expect(TestBed.inject(Router).url).toBe('/query/q-all?parentId=Companies%2F1&parentType=Company');
    expect(service.executeQuery.mock.calls.at(-1)[1].columns).toEqual([]);

    c.clearUrlScope();
    await settle(harness.fixture, { rounds: 6 });
    expect(TestBed.inject(Router).url).toBe('/query/q-all');
  });
});
