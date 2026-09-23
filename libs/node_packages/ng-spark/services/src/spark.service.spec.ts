import { TestBed } from '@angular/core/testing';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { provideHttpClient } from '@angular/common/http';
import { describe, expect, it, beforeEach, afterEach, vi } from 'vitest';

import { SparkService } from './spark.service';
import { RetryActionService } from './retry-action.service';
import { SparkClientOperationDispatcher } from '@mintplayer/ng-spark/client-operations';

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
    const promise = service.update('Person', 'p/1', { name: 'Bob' });

    const req = httpTesting.expectOne(r => r.method === 'POST' && r.url === '/spark/po/update');
    // `p/1` was `p%2F1` in a catch-all route segment, for no reason other than that a Raven id
    // contains a slash. In a body it is just the id.
    expect(req.request.body).toEqual({ objectTypeId: 'Person', id: 'p/1', persistentObject: { name: 'Bob' } });

    req.flush({ result: { id: 'p/1', name: 'Bob' }, operations: [] });
    await expect(promise).resolves.toMatchObject({ id: 'p/1' });
  });

  it('delete names its target in the body like every other call', async () => {
    const promise = service.delete('Person', 'p/1');

    // It used to be a DELETE that attached a body only once there were retry answers to send, and
    // a server that sniffed Content-Type to decide whether to read one. Both are gone.
    const req = httpTesting.expectOne(r => r.method === 'POST' && r.url === '/spark/po/delete');
    expect(req.request.body).toEqual({ objectTypeId: 'Person', id: 'p/1' });

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
});
