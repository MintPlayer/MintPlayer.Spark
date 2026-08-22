import { type EnvironmentProviders, inject, makeEnvironmentProviders } from '@angular/core';
import type { ClientOperation, DisableActionOperation, NotifyOperation, RefreshQueryOperation } from './operations';
import { SPARK_CLIENT_OPERATION_HANDLERS } from './handlers.token';
import { SparkNotificationService } from './notification.service';
import { SparkQueryRefreshService } from './query-refresh.service';

/**
 * Registers the built-in client-operation handlers: `notify` and `refreshQuery`.
 * Apps add this once in their bootstrap providers.
 *
 * Unregistered operation types are dropped SILENTLY by the dispatcher, which is why
 * `refreshQuery` did nothing at all for as long as it went unhandled — the server emitted
 * it, nothing listened, and no error said so. `disableAction` is in that state today: it is
 * registered below purely to log, so the gap is visible rather than invisible.
 *
 * To register custom operation types alongside the built-ins, add additional
 * `multi: true` providers using <see cref="SPARK_CLIENT_OPERATION_HANDLERS" />.
 */
export function provideSparkClientOperations(): EnvironmentProviders {
    return makeEnvironmentProviders([
        {
            provide: SPARK_CLIENT_OPERATION_HANDLERS,
            useFactory: () => {
                const notifications = inject(SparkNotificationService);
                return {
                    type: 'notify',
                    handler: (operation: ClientOperation) => {
                        const notify = operation as NotifyOperation;
                        notifications.show(notify.message, notify.kind, notify.durationMs);
                    },
                };
            },
            multi: true,
        },
        {
            provide: SPARK_CLIENT_OPERATION_HANDLERS,
            useFactory: () => {
                const refresh = inject(SparkQueryRefreshService);
                return {
                    type: 'refreshQuery',
                    handler: (operation: ClientOperation) => {
                        refresh.request((operation as RefreshQueryOperation).queryId);
                    },
                };
            },
            multi: true,
        },
        {
            provide: SPARK_CLIENT_OPERATION_HANDLERS,
            useFactory: () => ({
                type: 'disableAction',
                handler: (operation: ClientOperation) => {
                    // Deliberately a no-op with a warning, not silence. The server's
                    // IClientAccessor.DisableQueryActions presumes a client that honours it;
                    // nothing renders the disabled state yet, and a silently dropped operation
                    // reads as "the server did not send it" when debugging.
                    const disable = operation as DisableActionOperation;
                    console.warn(
                        `[spark] disableAction('${disable.actionName}') is not implemented by this client; the action stays enabled.`);
                },
            }),
            multi: true,
        },
    ]);
}
