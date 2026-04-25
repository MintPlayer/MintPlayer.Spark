import { type EnvironmentProviders, inject, makeEnvironmentProviders } from '@angular/core';
import type { ClientOperation, NotifyOperation } from './operations';
import { SPARK_CLIENT_OPERATION_HANDLERS } from './handlers.token';
import { SparkNotificationService } from './notification.service';

/**
 * Registers the built-in client-operation handlers. Currently registers `notify`;
 * additional types (`navigate`, `refreshQuery`, `refreshAttribute`, `disableAction`)
 * land in subsequent commits. Apps add this once in their bootstrap providers.
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
    ]);
}
