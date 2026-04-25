import { type EnvironmentProviders, inject, makeEnvironmentProviders } from '@angular/core';
import type { ClientInstruction, NotifyInstruction } from './instructions';
import { SPARK_CLIENT_INSTRUCTION_HANDLERS } from './handlers.token';
import { SparkNotificationService } from './notification.service';

/**
 * Registers the built-in client-instruction handlers. Currently registers `notify`;
 * additional types (`navigate`, `refreshQuery`, `refreshAttribute`, `disableAction`)
 * land in subsequent commits. Apps add this once in their bootstrap providers.
 *
 * To register custom instruction types alongside the built-ins, add additional
 * `multi: true` providers using <see cref="SPARK_CLIENT_INSTRUCTION_HANDLERS" />.
 */
export function provideSparkClientInstructions(): EnvironmentProviders {
    return makeEnvironmentProviders([
        {
            provide: SPARK_CLIENT_INSTRUCTION_HANDLERS,
            useFactory: () => {
                const notifications = inject(SparkNotificationService);
                return {
                    type: 'notify',
                    handler: (instruction: ClientInstruction) => {
                        const notify = instruction as NotifyInstruction;
                        notifications.show(notify.message, notify.kind, notify.durationMs);
                    },
                };
            },
            multi: true,
        },
    ]);
}
