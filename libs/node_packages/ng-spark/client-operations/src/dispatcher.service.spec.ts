import { TestBed } from '@angular/core/testing';
import { describe, expect, it, beforeEach, vi } from 'vitest';

import { SparkClientOperationDispatcher } from './dispatcher.service';
import { SPARK_CLIENT_OPERATION_HANDLERS } from './handlers.token';
import type { ClientOperation } from './operations';

/**
 * Pins the dispatcher contract documented in dispatcher.service.ts:
 *  - unknown operation types are silently dropped (forward-compat).
 *  - last-registered-wins on duplicate `type` values.
 *  - null/empty operation arrays are no-ops.
 *  - operations dispatch in array order.
 */
describe('SparkClientOperationDispatcher', () => {
  function configure(handlers: { type: string; handler: (op: ClientOperation) => void }[]) {
    TestBed.configureTestingModule({
      providers: [
        ...handlers.map(h => ({
          provide: SPARK_CLIENT_OPERATION_HANDLERS,
          useValue: h,
          multi: true,
        })),
      ],
    });
    return TestBed.inject(SparkClientOperationDispatcher);
  }

  beforeEach(() => TestBed.resetTestingModule());

  it('routes a known operation type to the matching handler', () => {
    const handler = vi.fn();
    const dispatcher = configure([{ type: 'notify', handler }]);

    dispatcher.dispatch([{ type: 'notify', message: 'hi' } as any]);

    expect(handler).toHaveBeenCalledOnce();
    expect(handler).toHaveBeenCalledWith({ type: 'notify', message: 'hi' });
  });

  it('drops unknown operation types silently (forward-compat)', () => {
    const handler = vi.fn();
    const dispatcher = configure([{ type: 'notify', handler }]);

    expect(() =>
      dispatcher.dispatch([{ type: 'future-op-introduced-by-server' } as any])
    ).not.toThrow();
    expect(handler).not.toHaveBeenCalled();
  });

  it('uses last-registered-wins when two handlers register for the same type', () => {
    const first = vi.fn();
    const second = vi.fn();
    const dispatcher = configure([
      { type: 'notify', handler: first },
      { type: 'notify', handler: second },
    ]);

    dispatcher.dispatch([{ type: 'notify' } as any]);

    expect(first).not.toHaveBeenCalled();
    expect(second).toHaveBeenCalledOnce();
  });

  it('preserves the array order when dispatching multiple operations', () => {
    const order: string[] = [];
    const dispatcher = configure([
      { type: 'a', handler: op => order.push((op as any).id) },
      { type: 'b', handler: op => order.push((op as any).id) },
    ]);

    dispatcher.dispatch([
      { type: 'a', id: '1' } as any,
      { type: 'b', id: '2' } as any,
      { type: 'a', id: '3' } as any,
    ]);

    expect(order).toEqual(['1', '2', '3']);
  });

  it('treats null operations as a no-op', () => {
    const handler = vi.fn();
    const dispatcher = configure([{ type: 'notify', handler }]);

    dispatcher.dispatch(null);
    dispatcher.dispatch(undefined);
    dispatcher.dispatch([]);

    expect(handler).not.toHaveBeenCalled();
  });

  it('works without any registered handlers (empty registry)', () => {
    TestBed.configureTestingModule({});
    const dispatcher = TestBed.inject(SparkClientOperationDispatcher);

    expect(() => dispatcher.dispatch([{ type: 'notify' } as any])).not.toThrow();
  });
});
