import { webcrypto } from 'node:crypto';
import { describe, expect, it } from 'vitest';
import { derivePbkdf2, fromBase64Url, isTicketCode, parseTicketPayload, verifyTicketSecret } from './ticket-secrets';

// Commerce #521 M15 (S8): the WebCrypto twin of the server's TicketSecrets. The vector is the one
// TicketSecretsTests.Pbkdf2_matches_the_published_HMAC_SHA256_vector checks in .NET.
const subtle = webcrypto.subtle as unknown as SubtleCrypto;

function toBase64Url(bytes: Uint8Array): string {
  return Buffer.from(bytes).toString('base64').replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_');
}

function hex(bytes: Uint8Array): string {
  return Buffer.from(bytes).toString('hex');
}

describe('ticket secrets', () => {
  it('derive the published PBKDF2-HMAC-SHA256 vector', async () => {
    const derived = await derivePbkdf2('password', new TextEncoder().encode('salt'), 1, subtle);
    expect(hex(derived)).toBe('120fb6cffcf8b32c43e7225256c4f837a86548c92ccc35480805987cb70be17b');
  });

  it('verify a secret against its verifier only', async () => {
    const secret = 'abcdefghijklmnopqrstuv';
    const salt = new Uint8Array(16).map((_, i) => i * 7);
    const hash = await derivePbkdf2(secret, salt, 1000, subtle);
    const verifier = { salt: toBase64Url(salt), hash: toBase64Url(hash), iterations: 1000 };

    expect(await verifyTicketSecret(verifier, secret, subtle)).toBe(true);
    expect(await verifyTicketSecret(verifier, 'abcdefghijklmnopqrstuw', subtle)).toBe(false);
    expect(await verifyTicketSecret({ ...verifier, iterations: 999 }, secret, subtle)).toBe(false);
    expect(await verifyTicketSecret({ ...verifier, hash: 'not+base64url' }, secret, subtle)).toBe(false);
    expect(await verifyTicketSecret(verifier, '', subtle)).toBe(false);
  });

  it('parse payloads exactly like the server', () => {
    const code = '0123456789ABCDEF';
    const secret = 'aaaaaaaaaaaaaaaaaaaaaa';
    expect(parseTicketPayload(`ST1.${code}.${secret}`)).toEqual({ code, secret });
    expect(parseTicketPayload(`  ${code.toLowerCase()}.${secret} `)).toEqual({ code, secret });
    for (const bad of [null, '', 'ST1.', 'ST1.ABC.def', `ST1.${code}.`, `ST1.${code}.short`, 'ST1.0123456789ABCDEU.' + secret, `ST1.${code}.aaaaaaaaaaaaaaaaaaaaa+`]) {
      expect(parseTicketPayload(bad)).toBeNull();
    }
  });

  it('know codes and base64url', () => {
    expect(isTicketCode('0123456789ABCDEF')).toBe(true);
    expect(isTicketCode('0123456789ABCDEI')).toBe(false);
    expect(isTicketCode('0123')).toBe(false);
    expect(fromBase64Url('AAEC')).toEqual(new Uint8Array([0, 1, 2]));
    expect(fromBase64Url('AAE')).toEqual(new Uint8Array([0, 1]));
    expect(fromBase64Url('A+')).toBeNull();
  });
});
