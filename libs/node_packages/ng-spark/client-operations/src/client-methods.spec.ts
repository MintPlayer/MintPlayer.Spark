import { InjectionToken, inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it, beforeEach, vi } from 'vitest';

import {
    SPARK_CLIENT_UNSUPPORTED_REASON,
    SparkClientMethodRegistry,
    type SparkClientMethodMap,
    provideSparkClientMethods,
} from './client-methods';

/**
 * The client half of a client-method retry (generic passkeys page, D7 and Q9): which methods can run
 * here, and running one so that every failure becomes a Cancel the server can handle.
 */
describe('SparkClientMethodRegistry', () => {
    beforeEach(() => TestBed.resetTestingModule());

    function registry(...registrations: SparkClientMethodMap[]) {
        TestBed.configureTestingModule({
            providers: [
                { provide: GREETING, useValue: 'hello' },
                ...registrations.map(r => provideSparkClientMethods(r)),
            ],
        });
        return TestBed.inject(SparkClientMethodRegistry);
    }

    it('dispatches to the registered method and resolves with its value', async () => {
        const invoke = vi.fn(async (args: unknown) => ({ got: args }));
        const methods = registry({ 'webauthn.create': { invoke } });

        await expect(methods.invoke('webauthn.create', { challenge: 'abc' }))
            .resolves.toEqual({ ok: true, value: { got: { challenge: 'abc' } } });
        expect(invoke).toHaveBeenCalledWith({ challenge: 'abc' });
    });

    it('runs the method in an injection context', async () => {
        const methods = registry({ 'greet': { invoke: async () => inject(GREETING) } });

        await expect(methods.invoke('greet', null)).resolves.toEqual({ ok: true, value: 'hello' });
    });

    it('fails closed for an unknown name', async () => {
        const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
        const methods = registry({});

        await expect(methods.invoke('webauthn.create', {})).resolves.toEqual({ ok: false });
        expect(warn).toHaveBeenCalled();
        warn.mockRestore();
    });

    it('fails closed for a rejection and for a thrown error', async () => {
        const methods = registry({
            'rejects': { invoke: () => Promise.reject(new DOMException('cancelled', 'NotAllowedError')) },
            'throws': { invoke: () => { throw new Error('boom'); } },
        });

        await expect(methods.invoke('rejects', {})).resolves.toEqual({ ok: false });
        await expect(methods.invoke('throws', {})).resolves.toEqual({ ok: false });
    });

    it('does not run a method that reports itself unsupported', async () => {
        const invoke = vi.fn(async () => 1);
        const methods = registry({ 'webauthn.create': { invoke, supported: () => false } });

        await expect(methods.invoke('webauthn.create', {})).resolves.toEqual({ ok: false });
        expect(invoke).not.toHaveBeenCalled();
    });

    it('a later registration of a name replaces an earlier one', async () => {
        const methods = registry(
            { 'pick': { invoke: async () => 'first' } },
            { 'pick': { invoke: async () => 'second' } },
        );

        await expect(methods.invoke('pick', null)).resolves.toEqual({ ok: true, value: 'second' });
    });

    describe('unavailableReason (requiresClient)', () => {
        it('is null for no requirement at all', () => {
            expect(registry({}).unavailableReason(undefined)).toBeNull();
        });

        it('is null for a registered, supported method', () => {
            const methods = registry({ 'a': { invoke: async () => 1 }, 'b': { invoke: async () => 1, supported: () => true } });
            expect(methods.unavailableReason('a')).toBeNull();
            expect(methods.unavailableReason('b')).toBeNull();
        });

        it('names the core reason for an unregistered method', () => {
            expect(registry({}).unavailableReason('webauthn.create')).toBe(SPARK_CLIENT_UNSUPPORTED_REASON);
        });

        it('names the method\'s own reason when it is unsupported, the core one otherwise', () => {
            const methods = registry({
                'own': { invoke: async () => 1, supported: () => false, unsupportedReason: 'auth.passkeysUnsupported' },
                'core': { invoke: async () => 1, supported: () => false },
                'throws': { invoke: async () => 1, supported: () => { throw new Error('no'); } },
            });
            expect(methods.unavailableReason('own')).toBe('auth.passkeysUnsupported');
            expect(methods.unavailableReason('core')).toBe(SPARK_CLIENT_UNSUPPORTED_REASON);
            expect(methods.unavailableReason('throws')).toBe(SPARK_CLIENT_UNSUPPORTED_REASON);
        });
    });
});

const GREETING = new InjectionToken<string>('GREETING');
