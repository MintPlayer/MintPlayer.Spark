import { Injectable, inject } from '@angular/core';
import type { ClientOperation } from './operations';
import { SPARK_CLIENT_OPERATION_HANDLERS, type ClientOperationHandler } from './handlers.token';

/**
 * Routes received operations to registered handlers. Unknown operation types
 * (no matching registration) are silently dropped — this is the forward-compat
 * contract that lets new operation types ship server-side without coordinated
 * client updates.
 *
 * Last-registered-wins on duplicate `type` values, matching standard
 * Angular multi-provider override semantics.
 */
@Injectable({ providedIn: 'root' })
export class SparkClientOperationDispatcher {
    private readonly handlerMap: ReadonlyMap<string, ClientOperationHandler>;

    constructor() {
        const registrations = inject(SPARK_CLIENT_OPERATION_HANDLERS, { optional: true }) ?? [];
        const map = new Map<string, ClientOperationHandler>();
        for (const { type, handler } of registrations) {
            map.set(type, handler);
        }
        this.handlerMap = map;
    }

    dispatch(operations: readonly ClientOperation[] | null | undefined): void {
        if (!operations || operations.length === 0) return;
        for (const operation of operations) {
            const handler = this.handlerMap.get(operation.type);
            if (handler) {
                handler(operation);
            }
            // Unknown types: silently dropped (forward-compat).
        }
    }
}
