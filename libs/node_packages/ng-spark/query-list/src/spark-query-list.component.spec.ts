import { TestBed, ComponentFixture } from '@angular/core/testing';
import { provideRouter, Routes } from '@angular/router';
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
import { StubComponent } from '../../src/test-utils';

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

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  for (let i = 0; i < 6; i++) {
    await fixture.whenStable();
    await Promise.resolve();
    fixture.detectChanges();
  }
  await fixture.whenStable();
}

async function setup(serviceOverrides: Record<string, unknown> = {}) {
  const service: any = {
    getEntityTypes: vi.fn().mockResolvedValue([personType]),
    getQueries: vi.fn().mockResolvedValue([allPeopleQuery]),
    getQuery: vi.fn().mockResolvedValue(allPeopleQuery),
    getPermissions: vi.fn().mockResolvedValue({ canQuery: true, canRead: true, canCreate: true, canEdit: true, canDelete: true }),
    getCustomActions: vi.fn().mockResolvedValue([]),
    executeQuery: vi.fn().mockResolvedValue({ data: [], totalRecords: 0 }),
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
  await settle(harness.fixture);
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
        data: [
          { id: 'people/1', name: 'Alice', objectTypeId: 't-person', attributes: [{ name: 'FirstName', value: 'Alice' }] },
          { id: 'people/2', name: 'Bob', objectTypeId: 't-person', attributes: [{ name: 'FirstName', value: 'Bob' }] },
        ],
      });
      await settle(harness.fixture);
      expect(c.gridData()).toHaveLength(2);

      streamSubject.next({ type: 'patch', updated: [{ id: 'people/1', attributes: { FirstName: 'Alicia' } }] });
      await settle(harness.fixture);

      const alice = c.gridData()!.find((i: any) => i.id === 'people/1')!;
      expect(alice.attributes.find((a: any) => a.name === 'FirstName')?.value).toBe('Alicia');
    });

    it('filters the snapshot client-side — there is no request to attach a search to', async () => {
      const { c, harness, streamSubject } = await live();
      streamSubject.next({
        type: 'snapshot',
        data: [
          { id: 'people/1', attributes: [{ name: 'FirstName', value: 'Alice' }] },
          { id: 'people/2', attributes: [{ name: 'FirstName', value: 'Bob' }] },
        ],
      });
      await settle(harness.fixture);

      c.searchTerm.set('bob');
      await settle(harness.fixture);

      expect(c.gridData()).toHaveLength(1);
      expect(c.gridData()![0].id).toBe('people/2');
    });

    it('surfaces a stream error', async () => {
      const { c, harness, streamSubject } = await live();

      streamSubject.error(new Error('socket died'));
      await settle(harness.fixture);

      expect(c.isStreaming()).toBe(false);
      expect(c.errorMessage()).toContain('socket died');
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
  });
});
