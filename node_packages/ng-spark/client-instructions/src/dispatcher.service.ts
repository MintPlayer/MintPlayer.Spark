import { Injectable, inject } from '@angular/core';
import type { ClientInstruction } from './instructions';
import { SPARK_CLIENT_INSTRUCTION_HANDLERS, type ClientInstructionHandler } from './handlers.token';

/**
 * Routes received instructions to registered handlers. Unknown instruction types
 * (no matching registration) are silently dropped — this is the forward-compat
 * contract that lets new instruction types ship server-side without coordinated
 * client updates.
 *
 * Last-registered-wins on duplicate `type` values, matching standard
 * Angular multi-provider override semantics.
 */
@Injectable({ providedIn: 'root' })
export class SparkClientInstructionDispatcher {
    private readonly handlerMap: ReadonlyMap<string, ClientInstructionHandler>;

    constructor() {
        const registrations = inject(SPARK_CLIENT_INSTRUCTION_HANDLERS, { optional: true }) ?? [];
        const map = new Map<string, ClientInstructionHandler>();
        for (const { type, handler } of registrations) {
            map.set(type, handler);
        }
        this.handlerMap = map;
    }

    dispatch(instructions: readonly ClientInstruction[] | null | undefined): void {
        if (!instructions || instructions.length === 0) return;
        for (const instruction of instructions) {
            const handler = this.handlerMap.get(instruction.type);
            if (handler) {
                handler(instruction);
            }
            // Unknown types: silently dropped (forward-compat).
        }
    }
}
