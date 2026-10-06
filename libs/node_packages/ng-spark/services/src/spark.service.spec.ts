import { TestBed } from '@angular/core/testing';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { provideHttpClient } from '@angular/common/http';
import { describe, expect, it, beforeEach, afterEach, vi } from 'vitest';

import { SparkService } from './spark.service';
import { RetryActionService } from './retry-action.service';
import { SparkClientOperationDispatcher, provideSparkClientMethods, type SparkClientMethodMap } from '@mintplayer/ng-spark/client-operations';
import { SPARK_CONFIG } from '@mintplayer/ng-spark';

/**
 * HTTP-shape tests for SparkService. The service is a thin client over a fixed
 * REST surface — these tests pin URL shape (path encoding, query params),
 * envelope unwrapping for mutations, dispatcher fan-out, and the 449/retry
 * recovery flow that the RetryActionService modal drives.
 */
describe('SparkService', () => {
  let service: SparkService;
  let httpTesting: HttpTestingController;
  let dispatcher: { dispatch: ReturnType<typeof vi.fn> };
  let retryService: { show: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    TestBed.resetTestingModule();
    dispatcher = { dispatch: vi.fn() };
    retryService = { show: vi.fn() };

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: SparkClientOperationDispatcher, useValue: dispatcher },
        { provide: RetryActionService, useValue: retryService },
      ],
    });
    service = TestBed.inject(SparkService);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpTesting.verify());

  // --- read-side: url shape + encoding ---------------------------------

  it('getEntityType encodes the id segment', async () => {
    const promise = service.getEntityType('Pers/On');
    httpTesting.expectOne('/spark/types/Pers%2FOn').flush({ id: 'Pers/On', name: 'Person' });
    await expect(promise).resolves.toMatchObject({ id: 'Pers/On' });
  });

  it('getEntityType passes the form purpose as ?for=', async () => {
    const promise = service.getEntityType('Person', 'new');
    httpTesting.expectOne('/spark/types/Person?for=new').flush({ id: 'Person', name: 'Person' });
    await expect(promise).resolves.toMatchObject({ id: 'Person' });
  });

  it('executeQuery serialises sortColumns and parent params correctly', async () => {
    const promise = service.executeQuery('q/1', {
      sortColumns: [
        { property: 'Name', direction: 'ascending' },
        { property: 'Age', direction: 'descending' },
      ],
      parentId: 'orders/1',
      parentType: 'Order',
      skip: 25,
      take: 10,
      search: 'al',
    });

    const req = httpTesting.expectOne(r => r.url === '/spark/queries/execute');
    expect(req.request.method).toBe('POST');
    // The parameters travel as a typed body now. `sortColumns` is a real array rather than the
    // `Name:asc,Age:desc` encoding a query string forced on it, and `q/1` needs no escaping.
    expect(req.request.body).toEqual({
      queryId: 'q/1',
      sortColumns: [
        { property: 'Name', direction: 'asc' },
        { property: 'Age', direction: 'desc' },
      ],
      parentId: 'orders/1',
      parentType: 'Order',
      skip: 25,
      take: 10,
      search: 'al',
    });

    req.flush({ data: [], totalRecords: 0, skip: 25, take: 10 });
    await expect(promise).resolves.toMatchObject({ skip: 25, take: 10 });
  });

  it('executeQuery sends per-column filters as includes and excludes', async () => {
    const promise = service.executeQuery('q/1', {
      columns: [
        { name: 'Region', includes: ['eu', 'us'] },
        { name: 'Status', excludes: ['archived'] },
      ],
    });

    const req = httpTesting.expectOne(r => r.url === '/spark/queries/execute');
    // Values, never labels: a reference column's values are document ids while its labels are
    // resolved breadcrumb text, and two rows may legitimately render the same text.
    expect(req.request.body.columns).toEqual([
      { name: 'Region', includes: ['eu', 'us'] },
      { name: 'Status', excludes: ['archived'] },
    ]);

    req.flush({ data: [], totalRecords: 0, skip: 0, take: 50 });
    await promise;
  });

  it('executeQuery omits columns entirely when no filter is applied', async () => {
    const promise = service.executeQuery('q/1', { columns: [] });

    const req = httpTesting.expectOne(r => r.url === '/spark/queries/execute');
    // An empty array is the absent filter, and sending one would make every unfiltered grid carry a
    // meaningless key. `undefined` is dropped from the JSON body entirely.
    expect(req.request.body.columns).toBeUndefined();

    req.flush({ data: [], totalRecords: 0, skip: 0, take: 50 });
    await promise;
  });

  it('getDistinctValues posts the column, the search term and the other columns\' filters', async () => {
    const promise = service.getDistinctValues('q/1', 'Manufacturer', {
      search: 'alf',
      columns: [{ name: 'Region', includes: ['eu'] }],
      parentId: 'cars/1',
      parentType: 'Car',
    });

    const req = httpTesting.expectOne(r => r.url === '/spark/queries/distinct-values');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({
      queryId: 'q/1',
      column: 'Manufacturer',
      search: 'alf',
      // The OTHER columns' filters: a panel must offer the values still reachable, not the ones
      // another column has already excluded every row for.
      columns: [{ name: 'Region', includes: ['eu'] }],
      parentId: 'cars/1',
      parentType: 'Car',
    });

    req.flush({ matching: [{ value: 'AlfaRomeo', label: 'Alfa Romeo' }], remaining: [], hasMore: false });
    await expect(promise).resolves.toMatchObject({ hasMore: false });
  });

  it('executeQueryByName resolves the query via /queries then executes it', async () => {
    const promise = service.executeQueryByName('AllPeople', { parentId: 'p/1' });

    httpTesting.expectOne('/spark/queries').flush([
      { id: 'q/all', name: 'AllPeople' },
      { id: 'q/other', name: 'Other' },
    ]);
    // Drain microtasks so the chained post to /queries/execute lands.
    await flushMicrotasks();

    const req = httpTesting.expectOne(r => r.url === '/spark/queries/execute');
    expect(req.request.body.queryId).toBe('q/all');
    expect(req.request.body.parentId).toBe('p/1');

    req.flush({ data: [], totalRecords: 0, skip: 0, take: 50 });
    await expect(promise).resolves.toMatchObject({ totalRecords: 0 });
  });

  it('executeQueryByName returns an empty result when the query name is unknown', async () => {
    const promise = service.executeQueryByName('UnknownQuery');
    httpTesting.expectOne('/spark/queries').flush([{ id: 'q/x', name: 'Other' }]);

    await expect(promise).resolves.toEqual({ columns: [], items: [], totalItems: 0, skip: 0, take: 50 });
  });

  // --- write-side: envelope unwrapping + dispatcher fan-out ------------

  it('create unwraps the ClientOperationEnvelope and dispatches operations', async () => {
    const promise = service.create('Person', { name: 'Alice' });

    const req = httpTesting.expectOne('/spark/po/create');
    expect(req.request.method).toBe('POST');
    // ⚠️ `objectTypeId` at the top level is the request parameter the server authorizes against.
    // It is NOT the same as a nested `persistentObject.objectTypeId`, which is submitted data the
    // server overwrites — see SparkRequestType. Moving it inside would be the confused-deputy bug.
    expect(req.request.body).toEqual({ objectTypeId: 'Person', persistentObject: { name: 'Alice' } });

    req.flush({
      result: { id: 'people/1', name: 'Alice' },
      operations: [{ type: 'notify', message: 'Created' }],
    });

    await expect(promise).resolves.toMatchObject({ id: 'people/1' });
    expect(dispatcher.dispatch).toHaveBeenCalledWith([
      { type: 'notify', message: 'Created' },
    ]);
  });

  it('update unwraps the envelope and names its target in the body', async () => {
    const promise = service.update('Person', 'p/1', { name: 'Bob', etag: 'A:1' });

    const req = httpTesting.expectOne(r => r.method === 'POST' && r.url === '/spark/po/update');
    // `p/1` was `p%2F1` in a catch-all route segment, for no reason other than that a Raven id
    // contains a slash. In a body it is just the id.
    expect(req.request.body).toEqual({ objectTypeId: 'Person', id: 'p/1', persistentObject: { name: 'Bob', etag: 'A:1' } });

    req.flush({ result: { id: 'p/1', name: 'Bob' }, operations: [] });
    await expect(promise).resolves.toMatchObject({ id: 'p/1' });
  });

  it('delete names its target in the body like every other call', async () => {
    const promise = service.delete('Person', 'p/1', 'A:1');

    // It used to be a DELETE that attached a body only once there were retry answers to send, and
    // a server that sniffed Content-Type to decide whether to read one. Both are gone.
    const req = httpTesting.expectOne(r => r.method === 'POST' && r.url === '/spark/po/delete');
    expect(req.request.body).toEqual({ objectTypeId: 'Person', id: 'p/1', etag: 'A:1' });

    req.flush({ result: undefined, operations: [] });
    await expect(promise).resolves.toBeUndefined();
  });

  // --- 449 retry-action recovery loop ----------------------------------

  it('handles a 449 by showing the retry modal then re-issuing with retryResults', async () => {
    retryService.show.mockResolvedValueOnce({ step: 'overwrite', option: 'Overwrite' });

    const promise = service.create('Person', { name: 'Alice' });

    httpTesting.expectOne('/spark/po/create').flush(
      {
        operations: [
          { type: 'notify', message: 'pre-retry' },
          {
            type: 'retry',
            step: 'overwrite',
            title: 'Overwrite?',
            options: ['Overwrite', 'Cancel'],
            defaultOption: 'Cancel',
          },
        ],
      },
      { status: 449, statusText: 'Retry With' }
    );

    // Drain microtasks so the catch handler runs, dispatcher fans out, and the
    // retry-modal mock resolves enough for retryFn() to issue the second request.
    await flushMicrotasks();

    expect(dispatcher.dispatch).toHaveBeenCalledWith([{ type: 'notify', message: 'pre-retry' }]);
    expect(retryService.show).toHaveBeenCalled();

    const second = httpTesting.expectOne('/spark/po/create');
    expect(second.request.body).toMatchObject({
      retryResults: [{ step: 'overwrite', option: 'Overwrite' }],
    });
    second.flush({ result: { id: 'people/1', name: 'Alice' }, operations: [] });

    await expect(promise).resolves.toMatchObject({ id: 'people/1' });
  });

  it('rethrows the 449 when the user cancels and Cancel was not an explicit option', async () => {
    retryService.show.mockResolvedValueOnce({ step: 'overwrite', option: 'Cancel' });

    const promise = service.create('Person', { name: 'Alice' });

    httpTesting.expectOne('/spark/po/create').flush(
      {
        operations: [{
          type: 'retry',
          step: 'overwrite',
          title: 'Overwrite?',
          options: ['Overwrite'], // Cancel NOT in options → cancellation rethrows.
        }],
      },
      { status: 449, statusText: 'Retry With' }
    );

    await expect(promise).rejects.toMatchObject({ status: 449 });
    await flushMicrotasks();
  });

  // ⚠️ A hook that raises a retry without checking Retry.Result first re-raises on every
  // resubmission. Without a bound the modal reopens forever: each round trip looks reasonable, the
  // tab spins until it is closed, and nothing is logged. The .NET client has had this bound since
  // M3; this is the browser's half of it.
  it('gives up on a hook that re-raises forever instead of looping', async () => {
    retryService.show.mockResolvedValue({ step: 'again', option: 'Yes' });

    const promise = service.create('Person', { name: 'Alice' });
    // Swallow here so the eventual rejection is never unhandled while we drive the loop below.
    const settled = promise.then(() => null, (e: Error) => e);

    const prompt = {
      operations: [{ type: 'retry', step: 'again', title: 'Again?', options: ['Yes'] }],
    };

    // 16 answers, then the 17th prompt trips the bound.
    for (let i = 0; i <= 16; i++) {
      httpTesting.expectOne('/spark/po/create').flush(prompt, { status: 449, statusText: 'Retry With' });
      await flushMicrotasks();
    }

    const error = await settled;
    expect(error).toBeInstanceOf(Error);
    expect((error as Error).message).toContain('Gave up after answering 16');
    expect((error as Error).message).toContain('Again?');
  });

  // Yields the microtask queue several times to let chained awaits resolve.
  function flushMicrotasks() {
    return new Promise<void>(r => setTimeout(r, 0));
  }

  it('rethrows non-449 errors without invoking the retry modal', async () => {
    const promise = service.create('Person', { name: 'Alice' });

    httpTesting.expectOne('/spark/po/create').flush('boom', { status: 500, statusText: 'Server Error' });

    await expect(promise).rejects.toMatchObject({ status: 500 });
    expect(retryService.show).not.toHaveBeenCalled();
  });

  // --- thin wrappers: verb + url + body --------------------------------

  it('getEntityTypes GETs the type list', async () => {
    const promise = service.getEntityTypes();
    const req = httpTesting.expectOne('/spark/types');
    expect(req.request.method).toBe('GET');
    req.flush([{ id: 't1' }]);
    await expect(promise).resolves.toEqual([{ id: 't1' }]);
  });

  it('getEntityTypeByClrType finds the type by its CLR name, or undefined', async () => {
    const types = [{ id: 't1', clrType: 'App.Car' }, { id: 't2', clrType: 'App.Person' }];

    const found = service.getEntityTypeByClrType('App.Person');
    httpTesting.expectOne('/spark/types').flush(types);
    await expect(found).resolves.toMatchObject({ id: 't2' });

    const missing = service.getEntityTypeByClrType('App.Nope');
    httpTesting.expectOne('/spark/types').flush(types);
    await expect(missing).resolves.toBeUndefined();
  });

  it('getPermissions GETs with the type id encoded', async () => {
    const promise = service.getPermissions('a/b c');
    const req = httpTesting.expectOne('/spark/permissions/a%2Fb%20c');
    expect(req.request.method).toBe('GET');
    req.flush({ canRead: true });
    await expect(promise).resolves.toEqual({ canRead: true });
  });

  it('getQuery POSTs the id in the body, unescaped', async () => {
    const promise = service.getQuery('q/1');
    const req = httpTesting.expectOne('/spark/queries/get');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ queryId: 'q/1' });
    req.flush({ id: 'q/1' });
    await expect(promise).resolves.toEqual({ id: 'q/1' });
  });

  it('getQueryByName fetches the catalogue once, however many names are resolved', async () => {
    const first = service.getQueryByName('A');
    const second = service.getQueryByName('B');
    httpTesting.expectOne('/spark/queries').flush([{ id: 'q/a', name: 'A' }, { id: 'q/b', name: 'B' }]);

    await expect(first).resolves.toMatchObject({ id: 'q/a' });
    await expect(second).resolves.toMatchObject({ id: 'q/b' });
    await expect(service.getQueryByName('C')).resolves.toBeUndefined();
    // afterEach's verify() fails on any further /spark/queries request.
  });

  it('getDistinctValues drops an empty search and empty column list', async () => {
    const promise = service.getDistinctValues('q/1', 'Color', { search: '', columns: [] });
    const req = httpTesting.expectOne('/spark/queries/distinct-values');
    expect(req.request.body).toEqual({
      queryId: 'q/1', column: 'Color', search: undefined, columns: undefined, parentId: undefined, parentType: undefined,
    });
    req.flush({ values: [] });
    await promise;
  });

  it('getProgramUnits GETs the program units', async () => {
    const promise = service.getProgramUnits();
    const req = httpTesting.expectOne('/spark/program-units');
    expect(req.request.method).toBe('GET');
    req.flush({ programUnitGroups: [] });
    await expect(promise).resolves.toEqual({ programUnitGroups: [] });
  });

  it('get POSTs the load request and returns the bare object (no envelope)', async () => {
    const promise = service.get('Car', 'cars/1');
    const req = httpTesting.expectOne('/spark/po/load');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ objectTypeId: 'Car', id: 'cars/1' });
    req.flush({ id: 'cars/1', attributes: [] });
    await expect(promise).resolves.toEqual({ id: 'cars/1', attributes: [] });
    expect(dispatcher.dispatch).not.toHaveBeenCalled();
  });

  it('get carries the soft-deletion mode when asked (#460)', async () => {
    const promise = service.get('Car', 'cars/1', { deleted: 'include' });
    const req = httpTesting.expectOne('/spark/po/load');
    expect(req.request.body).toEqual({ objectTypeId: 'Car', id: 'cars/1', deleted: 'include' });
    req.flush({ id: 'cars/1', attributes: [] });
    await promise;
  });

  it('refresh posts the object and the triggering attribute, and unwraps the envelope', async () => {
    const data = { attributes: [] } as any;
    const promise = service.refresh('Car', data, 'Jobs[2].ProfessionId');
    const req = httpTesting.expectOne('/spark/po/refresh');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ objectTypeId: 'Car', persistentObject: data, triggeredBy: 'Jobs[2].ProfessionId' });
    req.flush({ result: { id: 'x' }, operations: [] });
    await expect(promise).resolves.toEqual({ id: 'x' });
    // An empty operations list is not dispatched.
    expect(dispatcher.dispatch).not.toHaveBeenCalled();
  });

  it('newObject spreads its options into the body', async () => {
    const promise = service.newObject('Job', { parentType: 'Person' } as any);
    const req = httpTesting.expectOne('/spark/po/new');
    expect(req.request.body).toEqual({ objectTypeId: 'Job', parentType: 'Person' });
    req.flush({ result: { id: 'job-1' }, operations: [] });
    await expect(promise).resolves.toEqual({ id: 'job-1' });
  });

  it('newObject without options sends only the type', async () => {
    const promise = service.newObject('Job');
    const req = httpTesting.expectOne('/spark/po/new');
    expect(req.request.body).toEqual({ objectTypeId: 'Job' });
    req.flush({ result: null, operations: [] });
    await expect(promise).resolves.toBeNull();
  });

  it('deleteRow posts the row identity and resolves to nothing', async () => {
    const promise = service.deleteRow('Job', { rowKey: 'r1' } as any);
    const req = httpTesting.expectOne('/spark/po/delete-row');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ objectTypeId: 'Job', rowKey: 'r1' });
    req.flush({ result: { removed: true }, operations: [] });
    await expect(promise).resolves.toBeUndefined();
  });

  it('getCustomActions POSTs the type id', async () => {
    const promise = service.getCustomActions('Car');
    const req = httpTesting.expectOne('/spark/actions/list');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ objectTypeId: 'Car' });
    req.flush([{ name: 'Wash' }]);
    await expect(promise).resolves.toEqual([{ name: 'Wash' }]);
  });

  it('executeCustomAction sends the selection and names the query parent as id + type', async () => {
    const parent = { id: 'cars/1' } as any;
    const promise = service.executeCustomAction('Car', 'Wash', parent, ['cars/1', 'cars/2'], { id: 'companies/1', type: 'Company' }, 'q/cars');
    const req = httpTesting.expectOne('/spark/actions/execute');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({
      objectTypeId: 'Car', actionName: 'Wash', parent, selectedItemIds: ['cars/1', 'cars/2'],
      parentId: 'companies/1', parentType: 'Company', queryId: 'q/cars',
    });
    const operations = [{ type: 'notify', message: 'Washed', kind: 1 }];
    req.flush({ result: null, operations });
    await promise;
    expect(dispatcher.dispatch).toHaveBeenCalledWith(operations);
  });

  // --- #460 T5 / spike S7: a custom action returns data ---------------

  it('executeCustomAction resolves to the envelope result the action set', async () => {
    const promise = service.executeCustomAction<{ jobId: string }>('Car', 'Export');
    httpTesting.expectOne('/spark/actions/execute').flush({ result: { jobId: 'job-7' }, operations: [] });
    await expect(promise).resolves.toEqual({ jobId: 'job-7' });
  });

  it('executeCustomAction resolves to undefined when the action set nothing', async () => {
    const promise = service.executeCustomAction('Car', 'Wash');
    httpTesting.expectOne('/spark/actions/execute').flush({ result: null, operations: [] });
    await expect(promise).resolves.toBeUndefined();
  });

  it('executeCustomAction carries the result of the attempt that completes after a 449', async () => {
    retryService.show.mockResolvedValueOnce({ step: 0, option: 'Yes' });

    const promise = service.executeCustomAction<{ jobId: string }>('Car', 'Export');
    httpTesting.expectOne('/spark/actions/execute').flush(
      { result: null, operations: [{ type: 'retry', step: 0, title: 'Export?', options: ['Yes', 'No'] }] },
      { status: 449, statusText: 'Retry With' });

    await flushMicrotasks();

    const second = httpTesting.expectOne('/spark/actions/execute');
    expect(second.request.body).toMatchObject({ retryResults: [{ step: 0, option: 'Yes' }] });
    second.flush({ result: { jobId: 'job-8' }, operations: [] });

    await expect(promise).resolves.toEqual({ jobId: 'job-8' });
  });

  it('executeCustomAction without a query parent leaves parentId/parentType unset', async () => {
    const promise = service.executeCustomAction('Car', 'Wash');
    const req = httpTesting.expectOne('/spark/actions/execute');
    expect(req.request.body.parentId).toBeUndefined();
    expect(req.request.body.parentType).toBeUndefined();
    req.flush({ result: null, operations: [] });
    await promise;
  });

  describe('lookup references', () => {
    const value = { key: 'k', values: { en: 'K' }, isActive: true };

    it('getLookupReferences GETs the list', async () => {
      const promise = service.getLookupReferences();
      const req = httpTesting.expectOne('/spark/lookupref');
      expect(req.request.method).toBe('GET');
      req.flush([]);
      await expect(promise).resolves.toEqual([]);
    });

    it('getLookupReference GETs by encoded name', async () => {
      const promise = service.getLookupReference('Car Colors/2');
      const req = httpTesting.expectOne('/spark/lookupref/Car%20Colors%2F2');
      expect(req.request.method).toBe('GET');
      req.flush({ name: 'Car Colors/2', values: [] });
      await expect(promise).resolves.toMatchObject({ name: 'Car Colors/2' });
    });

    it('addLookupReferenceValue POSTs the value to the encoded name', async () => {
      const promise = service.addLookupReferenceValue('A/B', value);
      const req = httpTesting.expectOne('/spark/lookupref/A%2FB');
      expect(req.request.method).toBe('POST');
      expect(req.request.body).toEqual(value);
      req.flush(value);
      await expect(promise).resolves.toEqual(value);
    });

    it('updateLookupReferenceValue PUTs to name/key, both encoded', async () => {
      const promise = service.updateLookupReferenceValue('A/B', 'k?1', value);
      const req = httpTesting.expectOne('/spark/lookupref/A%2FB/k%3F1');
      expect(req.request.method).toBe('PUT');
      expect(req.request.body).toEqual(value);
      req.flush(value);
      await expect(promise).resolves.toEqual(value);
    });

    it('deleteLookupReferenceValue DELETEs name/key, both encoded', async () => {
      const promise = service.deleteLookupReferenceValue('A/B', 'k#1');
      const req = httpTesting.expectOne('/spark/lookupref/A%2FB/k%231');
      expect(req.request.method).toBe('DELETE');
      req.flush(null);
      await promise;
    });
  });

  it('prefixes every call with the configured base URL', async () => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: SPARK_CONFIG, useValue: { baseUrl: '/api/spark' } },
        { provide: SparkClientOperationDispatcher, useValue: dispatcher },
        { provide: RetryActionService, useValue: retryService },
      ],
    });
    service = TestBed.inject(SparkService);
    httpTesting = TestBed.inject(HttpTestingController);

    const promise = service.get('Car', 'cars/1');
    httpTesting.expectOne('/api/spark/po/load').flush({ id: 'cars/1' });
    await expect(promise).resolves.toEqual({ id: 'cars/1' });
  });

  it('a 449 on a read re-issues the read with the answer attached', async () => {
    retryService.show.mockResolvedValue({ step: 0, option: 'Yes' });
    const promise = service.get('Car', 'cars/1');

    httpTesting.expectOne('/spark/po/load').flush(
      { result: null, operations: [{ type: 'retry', step: 0, title: 'Sure?', options: ['Yes', 'No'] }] },
      { status: 449, statusText: 'Retry With' },
    );
    await flushMicrotasks();

    const second = httpTesting.expectOne('/spark/po/load');
    expect(second.request.body.retryResults).toEqual([{ step: 0, option: 'Yes' }]);
    second.flush({ id: 'cars/1' });
    await expect(promise).resolves.toEqual({ id: 'cars/1' });
  });

  it('a 449 without a retry operation dispatches the other operations and rethrows', async () => {
    const promise = service.create('Car', {});
    const notify = { type: 'notify', message: 'x', kind: 0 };

    httpTesting.expectOne('/spark/po/create').flush(
      { result: null, operations: [notify] },
      { status: 449, statusText: 'Retry With' },
    );

    await expect(promise).rejects.toMatchObject({ status: 449 });
    expect(dispatcher.dispatch).toHaveBeenCalledWith([notify]);
    expect(retryService.show).not.toHaveBeenCalled();
  });

  it('a 449 with no operations at all rethrows untouched', async () => {
    const promise = service.create('Car', {});
    httpTesting.expectOne('/spark/po/create').flush(null, { status: 449, statusText: 'Retry With' });
    await expect(promise).rejects.toMatchObject({ status: 449 });
    expect(dispatcher.dispatch).not.toHaveBeenCalled();
  });

  // --- client-method retries (IRetryAccessor.Invoke, generic passkeys page D7) ---------------

  describe('client-method retries', () => {
    function withMethods(methods: SparkClientMethodMap) {
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({
        providers: [
          provideHttpClient(),
          provideHttpClientTesting(),
          provideSparkClientMethods(methods),
          { provide: SparkClientOperationDispatcher, useValue: dispatcher },
          { provide: RetryActionService, useValue: retryService },
        ],
      });
      service = TestBed.inject(SparkService);
      httpTesting = TestBed.inject(HttpTestingController);
    }

    const invokeOp = (clientMethod: string, args: unknown = { challenge: 'abc' }) => ({
      result: null,
      operations: [{ type: 'retry', step: 0, title: clientMethod, options: [], clientMethod, arguments: args }],
    });

    it('awaits the registered method and re-sends with its value, without opening the modal', async () => {
      const invoke = vi.fn(async (args: unknown) => ({ id: 'cred-1', echoed: args }));
      withMethods({ 'webauthn.create': { invoke } });

      const promise = service.executeCustomAction('Passkeys', 'AddPasskey', { id: 'me' } as any);
      httpTesting.expectOne('/spark/actions/execute').flush(invokeOp('webauthn.create'), { status: 449, statusText: 'Retry With' });
      await flushMicrotasks();

      expect(invoke).toHaveBeenCalledWith({ challenge: 'abc' });
      expect(retryService.show).not.toHaveBeenCalled();

      const second = httpTesting.expectOne('/spark/actions/execute');
      expect(second.request.body.retryResults).toEqual([
        { step: 0, option: 'OK', value: { id: 'cred-1', echoed: { challenge: 'abc' } } },
      ]);
      second.flush({ result: null, operations: [] });
      await promise;
    });

    it('answers Cancel for a method the app never registered', async () => {
      const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
      withMethods({});

      const promise = service.executeCustomAction('Passkeys', 'AddPasskey', { id: 'me' } as any);
      httpTesting.expectOne('/spark/actions/execute').flush(invokeOp('webauthn.create'), { status: 449, statusText: 'Retry With' });
      await flushMicrotasks();

      const second = httpTesting.expectOne('/spark/actions/execute');
      expect(second.request.body.retryResults).toEqual([{ step: 0, option: 'Cancel' }]);
      expect(warn).toHaveBeenCalled();
      second.flush({ result: null, operations: [] });
      await promise;
      warn.mockRestore();
    });

    it('answers Cancel when the method rejects, and when it throws', async () => {
      withMethods({
        'webauthn.create': { invoke: () => Promise.reject(new DOMException('cancelled', 'NotAllowedError')) },
        'boom': { invoke: () => { throw new Error('boom'); } },
      });

      for (const name of ['webauthn.create', 'boom']) {
        const promise = service.executeCustomAction('Passkeys', 'AddPasskey', { id: 'me' } as any);
        httpTesting.expectOne('/spark/actions/execute').flush(invokeOp(name), { status: 449, statusText: 'Retry With' });
        await flushMicrotasks();

        const second = httpTesting.expectOne('/spark/actions/execute');
        expect(second.request.body.retryResults).toEqual([{ step: 0, option: 'Cancel' }]);
        second.flush({ result: null, operations: [] });
        await promise;
      }
    });

    it('answers Cancel without running a method that reports itself unsupported', async () => {
      const invoke = vi.fn(async () => 'never');
      withMethods({ 'webauthn.create': { invoke, supported: () => false } });

      const promise = service.executeCustomAction('Passkeys', 'AddPasskey', { id: 'me' } as any);
      httpTesting.expectOne('/spark/actions/execute').flush(invokeOp('webauthn.create'), { status: 449, statusText: 'Retry With' });
      await flushMicrotasks();

      expect(invoke).not.toHaveBeenCalled();
      const second = httpTesting.expectOne('/spark/actions/execute');
      expect(second.request.body.retryResults).toEqual([{ step: 0, option: 'Cancel' }]);
      second.flush({ result: null, operations: [] });
      await promise;
    });

    it('the depth cap still bounds a server that invokes forever', async () => {
      withMethods({ 'again': { invoke: async () => 1 } });

      const promise = service.executeCustomAction('Passkeys', 'AddPasskey', { id: 'me' } as any);
      const settled = promise.then(() => null, (e: Error) => e);

      for (let i = 0; i <= 16; i++) {
        httpTesting.expectOne('/spark/actions/execute').flush(invokeOp('again'), { status: 449, statusText: 'Retry With' });
        await flushMicrotasks();
      }

      const error = await settled;
      expect(error).toBeInstanceOf(Error);
      expect((error as Error).message).toContain('Gave up after answering 16');
    });
  });
});
