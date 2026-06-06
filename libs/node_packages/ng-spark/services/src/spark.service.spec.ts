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

    const req = httpTesting.expectOne(r => r.url === '/spark/queries/q%2F1/execute');
    expect(req.request.params.get('sortColumns')).toBe('Name:asc,Age:desc');
    expect(req.request.params.get('parentId')).toBe('orders/1');
    expect(req.request.params.get('parentType')).toBe('Order');
    expect(req.request.params.get('skip')).toBe('25');
    expect(req.request.params.get('take')).toBe('10');
    expect(req.request.params.get('search')).toBe('al');

    req.flush({ data: [], totalRecords: 0, skip: 25, take: 10 });
    await expect(promise).resolves.toMatchObject({ skip: 25, take: 10 });
  });

  it('executeQueryByName resolves the query via /queries then executes it', async () => {
    const promise = service.executeQueryByName('AllPeople', { parentId: 'p/1' });

    httpTesting.expectOne('/spark/queries').flush([
      { id: 'q/all', name: 'AllPeople' },
      { id: 'q/other', name: 'Other' },
    ]);
    // Drain microtasks so the chained http.get for /queries/{id}/execute lands.
    await flushMicrotasks();

    const req = httpTesting.expectOne(r => r.url === '/spark/queries/q%2Fall/execute');
    expect(req.request.params.get('parentId')).toBe('p/1');

    req.flush({ data: [], totalRecords: 0, skip: 0, take: 50 });
    await expect(promise).resolves.toMatchObject({ totalRecords: 0 });
  });

  it('executeQueryByName returns an empty result when the query name is unknown', async () => {
    const promise = service.executeQueryByName('UnknownQuery');
    httpTesting.expectOne('/spark/queries').flush([{ id: 'q/x', name: 'Other' }]);

    await expect(promise).resolves.toEqual({ data: [], totalRecords: 0, skip: 0, take: 50 });
  });

  // --- write-side: envelope unwrapping + dispatcher fan-out ------------

  it('create unwraps the ClientOperationEnvelope and dispatches operations', async () => {
    const promise = service.create('Person', { name: 'Alice' });

    const req = httpTesting.expectOne('/spark/po/Person');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ persistentObject: { name: 'Alice' } });

    req.flush({
      result: { id: 'people/1', name: 'Alice' },
      operations: [{ type: 'notify', message: 'Created' }],
    });

    await expect(promise).resolves.toMatchObject({ id: 'people/1' });
    expect(dispatcher.dispatch).toHaveBeenCalledWith([
      { type: 'notify', message: 'Created' },
    ]);
  });

  it('update unwraps the envelope on PUT', async () => {
    const promise = service.update('Person', 'p/1', { name: 'Bob' });

    httpTesting.expectOne(r => r.method === 'PUT' && r.url === '/spark/po/Person/p%2F1')
      .flush({ result: { id: 'p/1', name: 'Bob' }, operations: [] });

    await expect(promise).resolves.toMatchObject({ id: 'p/1' });
  });

  it('delete sends a body-less DELETE when there are no retry results', async () => {
    const promise = service.delete('Person', 'p/1');

    const req = httpTesting.expectOne(r => r.method === 'DELETE' && r.url === '/spark/po/Person/p%2F1');
    expect(req.request.body).toBeNull();

    req.flush({ result: undefined, operations: [] });
    await expect(promise).resolves.toBeUndefined();
  });

  // --- 449 retry-action recovery loop ----------------------------------

  it('handles a 449 by showing the retry modal then re-issuing with retryResults', async () => {
    retryService.show.mockResolvedValueOnce({ step: 'overwrite', option: 'Overwrite' });

    const promise = service.create('Person', { name: 'Alice' });

    httpTesting.expectOne('/spark/po/Person').flush(
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

    const second = httpTesting.expectOne('/spark/po/Person');
    expect(second.request.body).toMatchObject({
      retryResults: [{ step: 'overwrite', option: 'Overwrite' }],
    });
    second.flush({ result: { id: 'people/1', name: 'Alice' }, operations: [] });

    await expect(promise).resolves.toMatchObject({ id: 'people/1' });
  });

  it('rethrows the 449 when the user cancels and Cancel was not an explicit option', async () => {
    retryService.show.mockResolvedValueOnce({ step: 'overwrite', option: 'Cancel' });

    const promise = service.create('Person', { name: 'Alice' });

    httpTesting.expectOne('/spark/po/Person').flush(
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

  // Yields the microtask queue several times to let chained awaits resolve.
  function flushMicrotasks() {
    return new Promise<void>(r => setTimeout(r, 0));
  }

  it('rethrows non-449 errors without invoking the retry modal', async () => {
    const promise = service.create('Person', { name: 'Alice' });

    httpTesting.expectOne('/spark/po/Person').flush('boom', { status: 500, statusText: 'Server Error' });

    await expect(promise).rejects.toMatchObject({ status: 500 });
    expect(retryService.show).not.toHaveBeenCalled();
  });
});
