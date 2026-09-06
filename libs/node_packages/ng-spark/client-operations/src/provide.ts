import { type EnvironmentProviders, inject, makeEnvironmentProviders } from '@angular/core';
import { Router } from '@angular/router';
import type {
    ClientOperation,
    DisableActionOperation,
    NavigateOperation,
    NotifyOperation,
    RefreshAttributeOperation,
    RefreshQueryOperation,
} from './operations';
import { SPARK_CLIENT_OPERATION_HANDLERS } from './handlers.token';
import { SparkAttributeRefreshService } from './attribute-refresh.service';
import { SparkNotificationService } from './notification.service';
import { SparkQueryRefreshService } from './query-refresh.service';

/**
 * Registers the built-in client-operation handlers: `notify`, `refreshQuery`,
 * `refreshAttribute` and `navigate`. Apps add this once in their bootstrap providers.
 *
 * Unregistered operation types are dropped SILENTLY by the dispatcher, which is why
 * `refreshQuery` did nothing at all for as long as it went unhandled — the server emitted
 * it, nothing listened, and no error said so.
 *
 * That happened twice more before anyone noticed. `refreshAttribute` and `navigate` were both
 * declared wire types with no handler anywhere in the repository: every
 * `IClientAccessor.RefreshAttribute` and `Navigate` call in every Spark application was
 * computed, serialised, sent and discarded. Both are registered below. The remaining gap is
 * `disableAction`, registered purely to log so that it stays visible rather than invisible.
 *
 * The lesson worth keeping: a wire type in `operations.ts` is not a feature. Adding one without
 * a handler here produces a server API that appears to work and does nothing.
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
            useFactory: () => {
                const attributes = inject(SparkAttributeRefreshService);
                return {
                    type: 'refreshAttribute',
                    handler: (operation: ClientOperation) => {
                        const patch = operation as RefreshAttributeOperation;
                        attributes.request(patch.objectTypeId, patch.id, patch.attributeName, patch.value);
                    },
                };
            },
            multi: true,
        },
        {
            provide: SPARK_CLIENT_OPERATION_HANDLERS,
            useFactory: () => {
                const router = inject(Router);
                return {
                    type: 'navigate',
                    handler: (operation: ClientOperation) => {
                        const navigate = operation as NavigateOperation;

                        if (navigate.routeName) {
                            void router.navigateByUrl(
                                navigate.routeName.startsWith('/') ? navigate.routeName : `/${navigate.routeName}`);
                            return;
                        }

                        // `/po/{type}/{id}` is the convention the grid's row links already assume,
                        // and the detail route resolves a type by id as well as by alias, so the
                        // operation's objectTypeId can be used verbatim.
                        if (navigate.objectTypeId && navigate.id) {
                            void router.navigate(['/po', navigate.objectTypeId, navigate.id]);
                            return;
                        }

                        console.warn('[spark] navigate operation carried neither routeName nor objectTypeId+id; ignored.');
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
