import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { describe, expect, it, beforeEach, vi } from 'vitest';

import { provideSparkClientOperations } from './provide';
import { SPARK_CLIENT_OPERATION_HANDLERS } from './handlers.token';
import { SparkAttributeRefreshService } from './attribute-refresh.service';
import { SparkClientOperationDispatcher } from './dispatcher.service';
import type { ClientOperation } from './operations';

/**
 * Guards the gap that this file's own header warns about, and that the codebase then walked into
 * three separate times: a wire type in `operations.ts` with no handler registered here is dropped
 * silently by the dispatcher, so the server API appears to work and does nothing.
 *
 * `refreshQuery` was dead once. `refreshAttribute` and `navigate` were dead until this spec.
 * Asserting the registered SET — not just that individual handlers behave — is what makes the
 * next omission a failing test rather than a production mystery.
 */
describe('provideSparkClientOperations', () => {
  beforeEach(() => TestBed.resetTestingModule());

  function configure() {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideSparkClientOperations()],
    });
  }

  function registeredTypes(): string[] {
    configure();
    const handlers = TestBed.inject(SPARK_CLIENT_OPERATION_HANDLERS) as { type: string }[];
    return handlers.map(h => h.type).sort();
  }

  it('registers a handler for every operation the server can emit', () => {
    // If you add a wire type to operations.ts, add a handler in provide.ts and list it here.
    // Deliberately exact: an operation that reaches the dispatcher unregistered is invisible.
    expect(registeredTypes()).toEqual(
      ['disableAction', 'navigate', 'notify', 'refreshAttribute', 'refreshQuery'].sort());
  });

  it('refreshAttribute reaches the attribute-refresh service with the value the server sent', () => {
    configure();
    const dispatcher = TestBed.inject(SparkClientOperationDispatcher);
    const attributes = TestBed.inject(SparkAttributeRefreshService);

    expect(attributes.tokenFor('type-1', 'main')).toBe(0);

    dispatcher.dispatch([{
      type: 'refreshAttribute',
      objectTypeId: 'type-1',
      id: 'main',
      attributeName: 'AccountCount',
      value: 2,
    } as ClientOperation]);

    expect(attributes.tokenFor('type-1', 'main')).toBe(1);
    expect(attributes.patchesFor('type-1', 'main')).toEqual({ AccountCount: 2 });
    // A different object must not see it.
    expect(attributes.patchesFor('type-2', 'main')).toEqual({});
  });

  it('navigate by objectTypeId + id goes to the detail route', () => {
    configure();
    const dispatcher = TestBed.inject(SparkClientOperationDispatcher);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);

    dispatcher.dispatch([{ type: 'navigate', objectTypeId: 'type-1', id: 'Repositories/7' } as ClientOperation]);

    expect(navigate).toHaveBeenCalledWith(['/po', 'type-1', 'Repositories/7']);
  });

  it('navigate by routeName goes to that url, leading slash or not', () => {
    configure();
    const dispatcher = TestBed.inject(SparkClientOperationDispatcher);
    const byUrl = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);

    dispatcher.dispatch([{ type: 'navigate', routeName: 'a/MintPlayer' } as ClientOperation]);
    expect(byUrl).toHaveBeenCalledWith('/a/MintPlayer');

    dispatcher.dispatch([{ type: 'navigate', routeName: '/a/MintPlayer' } as ClientOperation]);
    expect(byUrl).toHaveBeenLastCalledWith('/a/MintPlayer');
  });
});
