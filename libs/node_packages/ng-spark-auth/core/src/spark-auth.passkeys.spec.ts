import { TestBed } from '@angular/core/testing';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { provideHttpClient } from '@angular/common/http';
import { describe, expect, it, beforeEach, afterEach, vi } from 'vitest';

import { SparkAuthService } from './spark-auth.service';
import { SPARK_AUTH_CONFIG, defaultSparkAuthConfig, passkeysSupported } from '@mintplayer/ng-spark-auth/models';

const flush = () => new Promise<void>((resolve) => setTimeout(resolve, 0));

/**
 * jsdom implements none of WebAuthn, so the whole surface is stubbed. What is being tested is the
 * service's own logic — which endpoints it calls, what it does and does not send, and how it
 * collapses failures — not the browser's.
 */
function installWebAuthn(overrides: {
  create?: () => Promise<unknown>;
  get?: () => Promise<unknown>;
} = {}) {
  const parseCreation = vi.fn((json: unknown) => json);
  const parseRequest = vi.fn((json: unknown) => json);

  (globalThis as Record<string, unknown>)['PublicKeyCredential'] = Object.assign(function () { }, {
    parseCreationOptionsFromJSON: parseCreation,
    parseRequestOptionsFromJSON: parseRequest,
  });

  const create = overrides.create ?? (async () => ({ id: 'new-credential' }));
  const get = overrides.get ?? (async () => ({ id: 'existing-credential' }));

  Object.defineProperty(globalThis.navigator, 'credentials', {
    value: { create: vi.fn(create), get: vi.fn(get) },
    configurable: true,
  });

  Object.defineProperty(globalThis.window, 'isSecureContext', { value: true, configurable: true });

  return { parseCreation, parseRequest };
}

function removeWebAuthn() {
  delete (globalThis as Record<string, unknown>)['PublicKeyCredential'];
  Object.defineProperty(globalThis.navigator, 'credentials', { value: undefined, configurable: true });
}

describe('SparkAuthService passkeys', () => {
  let service: SparkAuthService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: SPARK_AUTH_CONFIG, useValue: defaultSparkAuthConfig },
      ],
    });

    service = TestBed.inject(SparkAuthService);
    http = TestBed.inject(HttpTestingController);

    // The constructor's checkAuth() fires first and would otherwise be mistaken for a ceremony call.
    http.expectOne('/spark/auth/me').flush(null);
  });

  afterEach(() => {
    removeWebAuthn();
    http.verify();
  });

  describe('feature detection', () => {
    it('refuses both ceremonies when the browser cannot run them', async () => {
      removeWebAuthn();

      await expect(service.registerPasskey()).resolves.toEqual({ success: false, error: 'unsupported' });
      await expect(service.signInWithPasskey()).resolves.toEqual({ success: false, error: 'unsupported' });
    });

    it('reports unsupported rather than throwing on an insecure origin', () => {
      installWebAuthn();
      Object.defineProperty(globalThis.window, 'isSecureContext', { value: false, configurable: true });

      expect(passkeysSupported()).toBe(false);
    });
  });

  describe('sign-in', () => {
    it('never sends a username with the request options', async () => {
      installWebAuthn();

      const result = service.signInWithPasskey();
      await flush();

      const options = http.expectOne('/spark/auth/passkeys/request-options');
      expect(options.request.method).toBe('POST');
      // The whole point of D10: no identity is offered, so the response cannot vary by account.
      expect(options.request.urlWithParams).toBe('/spark/auth/passkeys/request-options');
      expect(options.request.body).toEqual({});
      options.flush(JSON.stringify({ challenge: 'abc' }));
      await flush();

      http.expectOne('/spark/auth/passkeys/sign-in').flush(null);
      await flush();
      http.expectOne('/spark/auth/csrf-refresh').flush(null);
      await flush();
      http.expectOne('/spark/auth/me').flush({ isAuthenticated: true, userName: 'a', email: 'a', roles: [] });

      await expect(result).resolves.toEqual({ success: true });
    });

    it('signs the user in and refreshes the session state', async () => {
      installWebAuthn();

      const result = service.signInWithPasskey();
      await flush();
      http.expectOne('/spark/auth/passkeys/request-options').flush(JSON.stringify({ challenge: 'abc' }));
      await flush();
      http.expectOne('/spark/auth/passkeys/sign-in').flush(null);
      await flush();
      http.expectOne('/spark/auth/csrf-refresh').flush(null);
      await flush();
      http.expectOne('/spark/auth/me').flush({ isAuthenticated: true, userName: 'jane', email: 'j@e.com', roles: [] });

      await expect(result).resolves.toEqual({ success: true });
      expect(service.isAuthenticated()).toBe(true);
    });

    it('treats a dismissed prompt as cancelled, not as an error', async () => {
      installWebAuthn({ get: async () => { throw new DOMException('aborted', 'AbortError'); } });

      const result = service.signInWithPasskey();
      await flush();
      http.expectOne('/spark/auth/passkeys/request-options').flush(JSON.stringify({ challenge: 'abc' }));

      await expect(result).resolves.toEqual({ success: false, error: 'cancelled' });
    });

    it('reports no_credential when the authenticator produces nothing', async () => {
      installWebAuthn({ get: async () => null });

      const result = service.signInWithPasskey();
      await flush();
      http.expectOne('/spark/auth/passkeys/request-options').flush(JSON.stringify({ challenge: 'abc' }));

      await expect(result).resolves.toEqual({ success: false, error: 'no_credential' });
    });

    it('surfaces a lockout, the one server outcome worth distinguishing', async () => {
      installWebAuthn();

      const result = service.signInWithPasskey();
      await flush();
      http.expectOne('/spark/auth/passkeys/request-options').flush(JSON.stringify({ challenge: 'abc' }));
      await flush();
      http.expectOne('/spark/auth/passkeys/sign-in')
        .flush({ error: 'locked_out' }, { status: 401, statusText: 'Unauthorized' });

      await expect(result).resolves.toEqual({ success: false, error: 'locked_out' });
    });

    it('collapses an ordinary refusal to failed', async () => {
      installWebAuthn();

      const result = service.signInWithPasskey();
      await flush();
      http.expectOne('/spark/auth/passkeys/request-options').flush(JSON.stringify({ challenge: 'abc' }));
      await flush();
      http.expectOne('/spark/auth/passkeys/sign-in')
        .flush({ error: 'passkey_failed' }, { status: 401, statusText: 'Unauthorized' });

      await expect(result).resolves.toEqual({ success: false, error: 'failed' });
    });
  });

  describe('enrollment and management', () => {
    it('posts the credential and the chosen name', async () => {
      installWebAuthn();

      const result = service.registerPasskey('Work laptop');
      await flush();
      http.expectOne('/spark/auth/passkeys/creation-options').flush(JSON.stringify({ challenge: 'abc' }));
      await flush();

      const enroll = http.expectOne('/spark/auth/passkeys');
      expect(enroll.request.method).toBe('POST');
      expect(enroll.request.body.name).toBe('Work laptop');
      expect(JSON.parse(enroll.request.body.credentialJson).id).toBe('new-credential');
      enroll.flush({ id: 'abc', name: 'Work laptop', createdAt: '', isBackedUp: false, isBackupEligible: true, transports: [] });
      await flush();
      http.expectOne('/spark/auth/csrf-refresh').flush(null);

      await expect(result).resolves.toMatchObject({ success: true, passkey: { name: 'Work laptop' } });
    });

    it('lists the account passkeys', async () => {
      const result = service.passkeys();
      await flush();
      http.expectOne('/spark/auth/passkeys').flush([{ id: 'a', name: 'Phone' }]);

      await expect(result).resolves.toHaveLength(1);
    });

    it('reports last_credential rather than throwing when removal is refused', async () => {
      const result = service.removePasskey('abc');
      await flush();
      http.expectOne('/spark/auth/passkeys/abc')
        .flush({ success: false, error: 'last_credential' }, { status: 400, statusText: 'Bad Request' });

      await expect(result).resolves.toEqual({ success: false, error: 'last_credential' });
    });

    it('encodes the credential id into the url', async () => {
      const result = service.renamePasskey('a/b+c', 'Renamed');
      await flush();
      http.expectOne('/spark/auth/passkeys/a%2Fb%2Bc/name').flush({});

      await expect(result).resolves.toEqual({ success: true });
    });
  });
});
