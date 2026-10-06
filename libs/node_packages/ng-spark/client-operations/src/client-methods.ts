import {
    EnvironmentInjector,
    type EnvironmentProviders,
    Injectable,
    InjectionToken,
    inject,
    makeEnvironmentProviders,
    runInInjectionContext,
} from '@angular/core';

/**
 * One step a server action needs from the browser (`IRetryAccessor.Invoke` on the server): WebAuthn,
 * a file picker, the clipboard. The answer travels back into the same server action as the retry's
 * `value`, so authorization, state and the follow-up all stay inside that one action.
 */
export interface SparkClientMethod {
    /**
     * Runs the step. Called in an injection context, so `inject()` works synchronously at the top.
     * A rejection or a thrown error answers the server with `Cancel`, exactly like an unknown name.
     */
    invoke(args: unknown): Promise<unknown>;
    /**
     * Whether this browser can run the method at all. An action whose `requiresClient` names a method
     * that is unsupported is shown disabled; an `Invoke` of it answers `Cancel` without running it.
     * Absent means always supported.
     */
    supported?(): boolean;
    /** The translation key explaining why the method is unsupported; defaults to `common.clientUnsupported`. */
    unsupportedReason?: string;
}

/** Client methods by the name the server invokes them with, e.g. `{ 'webauthn.create': { invoke: … } }`. */
export type SparkClientMethodMap = Readonly<Record<string, SparkClientMethod>>;

/** Multi-provider token; use {@link provideSparkClientMethods}. A later registration of a name wins. */
export const SPARK_CLIENT_METHODS = new InjectionToken<readonly SparkClientMethodMap[]>('SPARK_CLIENT_METHODS');

/** The reason shown on an action whose client method is missing or unsupported, when the method names none. */
export const SPARK_CLIENT_UNSUPPORTED_REASON = 'common.clientUnsupported';

/**
 * Registers client methods a server action may invoke through a retry (`IRetryAccessor.Invoke`).
 *
 * The set is closed on purpose: the server can only name a method the app registered here, and an
 * unknown name answers `Cancel`. Nothing the server sends is evaluated.
 *
 * @example
 * provideSparkClientMethods({
 *   'clipboard.read': { invoke: () => navigator.clipboard.readText(), supported: () => !!navigator.clipboard },
 * })
 */
export function provideSparkClientMethods(methods: SparkClientMethodMap): EnvironmentProviders {
    return makeEnvironmentProviders([{ provide: SPARK_CLIENT_METHODS, useValue: methods, multi: true }]);
}

/** How a client method ended: its value, or a failure the server sees as `Cancel`. */
export type SparkClientMethodOutcome = { ok: true; value: unknown } | { ok: false };

/** The registered client methods: whether one can run here, and running it. */
@Injectable({ providedIn: 'root' })
export class SparkClientMethodRegistry {
    private readonly injector = inject(EnvironmentInjector);
    private readonly methods: ReadonlyMap<string, SparkClientMethod>;

    constructor() {
        const methods = new Map<string, SparkClientMethod>();
        for (const registration of inject(SPARK_CLIENT_METHODS, { optional: true }) ?? []) {
            for (const [name, method] of Object.entries(registration))
                methods.set(name, method);
        }
        this.methods = methods;
    }

    /**
     * Null when the method is registered and supported here; otherwise the translation key saying why
     * it is not. Null for no name at all, so an action without `requiresClient` is never affected.
     */
    unavailableReason(name: string | null | undefined): string | null {
        if (!name) return null;
        const method = this.methods.get(name);
        if (!method) return SPARK_CLIENT_UNSUPPORTED_REASON;
        return this.isSupported(method) ? null : (method.unsupportedReason ?? SPARK_CLIENT_UNSUPPORTED_REASON);
    }

    /**
     * Runs the method registered under `name`. Fails closed: an unknown name, an unsupported method, a
     * rejection or a thrown error all end as `{ ok: false }`, which the retry loop answers as `Cancel`.
     */
    async invoke(name: string, args: unknown): Promise<SparkClientMethodOutcome> {
        const method = this.methods.get(name);
        if (!method) {
            // A server naming a method this app never registered is a wiring mistake worth a line in
            // the console; a user cancelling a ceremony is not, so rejections stay quiet.
            console.warn(`[spark] the server invoked the client method '${name}', which is not registered; answered Cancel.`);
            return { ok: false };
        }
        if (!this.isSupported(method)) return { ok: false };

        try {
            const value = await runInInjectionContext(this.injector, () => method.invoke(args));
            return { ok: true, value };
        } catch {
            return { ok: false };
        }
    }

    private isSupported(method: SparkClientMethod): boolean {
        if (!method.supported) return true;
        try {
            return runInInjectionContext(this.injector, () => method.supported!()) === true;
        } catch {
            return false;
        }
    }
}
