import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { SPARK_CLIENT_METHODS, SparkClientMethodRegistry } from '@mintplayer/ng-spark/client-operations';

import { sparkAuthClientMethods } from './webauthn-client-methods';
import { provideSpark } from '@mintplayer/ng-spark';
import { withAuth } from './provide-spark-auth';
import { SparkAuthService } from '@mintplayer/ng-spark/auth/core';
import type { SparkExternalLoginResult } from '@mintplayer/ng-spark/auth/models';

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

describe("ng-spark/auth's webauthn.create client method", () => {
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

  it('is registered by withAuth()', () => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideSpark(withAuth())] });

    const maps = TestBed.inject(SPARK_CLIENT_METHODS);
    expect(maps.some(map => map['webauthn.create'] === method)).toBe(true);

    installWebAuthn(async () => null);
    expect(TestBed.inject(SparkClientMethodRegistry).unavailableReason('webauthn.create')).toBeNull();
  });
});

describe("ng-spark/auth's oauth.authorize client method (#554)", () => {
  const oauth = sparkAuthClientMethods['oauth.authorize'];
  const args = { url: 'https://github.com/login/oauth/authorize?client_id=x', nonce: 'n0nce-n0nce-n0nce-n0nce', handle: 'the-handle' };

  function run(result: SparkExternalLoginResult, input: unknown = args) {
    const authorizeInPopup = vi.fn(async () => result);
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [{ provide: SparkAuthService, useValue: { authorizeInPopup } }] });
    return { answer: TestBed.runInInjectionContext(() => oauth.invoke(input)), authorizeInPopup };
  }

  it('opens the server URL with its nonce and answers only the handle', async () => {
    const { answer, authorizeInPopup } = run({ success: true });

    expect(await answer).toEqual({ handle: 'the-handle' });
    expect(authorizeInPopup).toHaveBeenCalledWith(args.url, args.nonce);
  });

  it('answers the handle on a reported failure too: the server knows the outcome', async () => {
    expect(await run({ success: false, error: 'no_login_info' }).answer).toEqual({ handle: 'the-handle' });
  });

  it('says when the popup was blocked', async () => {
    expect(await run({ success: false, error: 'popup_blocked' }).answer).toEqual({ error: 'popup_blocked' });
  });

  it('rejects when no answer came in time, so the server sees Cancel', async () => {
    await expect(run({ success: false, error: 'popup_closed' }).answer).rejects.toThrow();
  });

  it('opens nothing but a web page', async () => {
    const { answer, authorizeInPopup } = run({ success: true }, { ...args, url: 'javascript:alert(1)' });

    await expect(answer).rejects.toThrow();
    expect(authorizeInPopup).not.toHaveBeenCalled();
  });

  it('is registered by withAuth()', () => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideSpark(withAuth())] });

    expect(TestBed.inject(SPARK_CLIENT_METHODS).some(map => map['oauth.authorize'] === oauth)).toBe(true);
  });
});
