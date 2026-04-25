import { InjectionToken } from '@angular/core';
import type { ClientOperation } from './operations';

/**
 * A handler for a specific operation type. Receives the operation and
 * executes the side-effect (e.g. show a toast, navigate, refresh a query).
 * Handlers should `as`-narrow the operation to the type they registered for.
 */
export type ClientOperationHandler = (operation: ClientOperation) => void;

/**
 * One entry in the multi-provider registration. Apps can register custom
 * handlers alongside the built-in ones to extend the operation set with
 * app-specific operation types.
 */
export interface ClientOperationHandlerRegistration {
    type: string;
    handler: ClientOperationHandler;
}

/**
 * Multi-provider token. `provideSparkClientOperations()` registers the
 * built-in handlers; apps can add their own with additional `multi: true`
 * providers using this token.
 */
export const SPARK_CLIENT_OPERATION_HANDLERS = new InjectionToken<readonly ClientOperationHandlerRegistration[]>(
    'SPARK_CLIENT_OPERATION_HANDLERS',
);
