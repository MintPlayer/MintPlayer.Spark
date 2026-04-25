import { InjectionToken } from '@angular/core';
import type { ClientInstruction } from './instructions';

/**
 * A handler for a specific instruction type. Receives the instruction and
 * executes the side-effect (e.g. show a toast, navigate, refresh a query).
 * Handlers should `as`-narrow the instruction to the type they registered for.
 */
export type ClientInstructionHandler = (instruction: ClientInstruction) => void;

/**
 * One entry in the multi-provider registration. Apps can register custom
 * handlers alongside the built-in ones to extend the instruction set with
 * app-specific instruction types.
 */
export interface ClientInstructionHandlerRegistration {
    type: string;
    handler: ClientInstructionHandler;
}

/**
 * Multi-provider token. `provideSparkClientInstructions()` registers the
 * built-in handlers; apps can add their own with additional `multi: true`
 * providers using this token.
 */
export const SPARK_CLIENT_INSTRUCTION_HANDLERS = new InjectionToken<readonly ClientInstructionHandlerRegistration[]>(
    'SPARK_CLIENT_INSTRUCTION_HANDLERS',
);
