import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { SPARK_CLIENT_METHODS, SparkClientMethodRegistry } from '@mintplayer/ng-spark/client-operations';

import { sparkAuthClientMethods } from './webauthn-client-methods';
import { provideSparkAuth } from './provide-spark-auth';

/** jsdom implements none of WebAuthn, so it is stubbed; what is tested is the method's own logic. */
function installWebAuthn(create: () => Promise<unknown>) {
  const parseCreation = vi.fn((json: unknown) => ({ parsed: json }));
  (globalThis as Record<string, unknown>)['PublicKeyCredential'] = Object.assign(function () { }, {
    parseCreationOptionsFromJSON: parseCreation,
    parseRequestOptionsFromJSON: vi.fn(),
  });
  const createSpy = vi.fn(create);
  Object.defineProperty(globalThis.navigator, 'credentials', {
    value: { create: createSpy, get: vi.fn() },
    configurable: true,
  });
  Object.defineProperty(globalThis.window, 'isSecureContext', { value: true, configurable: true });
  return { parseCreation, create: createSpy };
}

function removeWebAuthn() {
  delete (globalThis as Record<string, unknown>)['PublicKeyCredential'];
  Object.defineProperty(globalThis.navigator, 'credentials', { value: undefined, configurable: true });
}

const method = sparkAuthClientMethods['webauthn.create'];

describe("ng-spark-auth's webauthn.create client method", () => {
  afterEach(() => removeWebAuthn());

  it('parses the server options, runs the ceremony and answers the credential as plain JSON', async () => {
    const credential = { id: 'cred', toJSON: () => ({ id: 'cred', type: 'public-key' }) };
    const { parseCreation, create } = installWebAuthn(async () => credential);

    const answer = await method.invoke({ challenge: 'abc' });

    expect(parseCreation).toHaveBeenCalledWith({ challenge: 'abc' });
    expect(create).toHaveBeenCalledWith({ publicKey: { parsed: { challenge: 'abc' } } });
    expect(answer).toEqual({ id: 'cred', type: 'public-key' });
  });

  it('rejects a dismissed prompt, so the server sees Cancel and shows no error', async () => {
    installWebAuthn(async () => { throw new DOMException('dismissed', 'AbortError'); });
    await expect(method.invoke({})).rejects.toBeInstanceOf(DOMException);

    installWebAuthn(async () => { throw new DOMException('no credential', 'NotAllowedError'); });
    await expect(method.invoke({})).rejects.toBeInstanceOf(DOMException);

    installWebAuthn(async () => null);
    await expect(method.invoke({})).rejects.toBeInstanceOf(DOMException);
  });

  it('answers any other failure as an error code, so the server can say it did not work', async () => {
    installWebAuthn(async () => { throw new DOMException('already registered', 'InvalidStateError'); });

    expect(await method.invoke({})).toEqual({ error: 'failed' });
  });

  it('is unsupported without WebAuthn, and names its own reason', () => {
    removeWebAuthn();
    expect(method.supported!()).toBe(false);
    expect(method.unsupportedReason).toBe('auth.passkeyUnsupported');

    installWebAuthn(async () => null);
    expect(method.supported!()).toBe(true);
  });

  it('is registered by provideSparkAuth()', () => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideSparkAuth()] });

    const maps = TestBed.inject(SPARK_CLIENT_METHODS);
    expect(maps.some(map => map['webauthn.create'] === method)).toBe(true);

    installWebAuthn(async () => null);
    expect(TestBed.inject(SparkClientMethodRegistry).unavailableReason('webauthn.create')).toBeNull();
  });
});
