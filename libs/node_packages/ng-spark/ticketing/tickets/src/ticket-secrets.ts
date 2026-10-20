// The ticket secret on the door device (commerce PRD §4.11, default 9, S8): the TypeScript twin of the server's
// TicketSecrets. PBKDF2-HMAC-SHA256 is native in WebCrypto, so the offline scanner verifies a scanned QR code against
// the list's verifier with no library (K11). The list never carries the secret itself.

/** The QR payload's version prefix (`ST1.{code}.{secret}`). */
export const TICKET_QR_PREFIX = 'ST1.';

const CODE_ALPHABET = '0123456789ABCDEFGHJKMNPQRSTVWXYZ'; // Crockford base 32
const BASE64URL = /^[A-Za-z0-9_-]+$/;

/** Whether `code` has the shape of a ticket code (16 Crockford base-32 characters). */
export function isTicketCode(code: string | null | undefined): boolean {
  return !!code && code.length === 16 && [...code].every(c => CODE_ALPHABET.includes(c));
}

/**
 * Splits a scanned payload into code and secret, as the server does: with or without the `ST1.` prefix, trimmed, the
 * code upper-cased, the secret case-sensitive (16–64 base64url characters). `null` for anything else.
 */
export function parseTicketPayload(payload: string | null | undefined): { code: string; secret: string } | null {
  if (!payload) return null;
  let text = payload.trim();
  if (text.toUpperCase().startsWith(TICKET_QR_PREFIX)) text = text.slice(TICKET_QR_PREFIX.length);
  const dot = text.indexOf('.');
  if (dot <= 0 || dot === text.length - 1) return null;
  const code = text.slice(0, dot).toUpperCase();
  const secret = text.slice(dot + 1);
  if (!isTicketCode(code) || secret.length < 16 || secret.length > 64 || !BASE64URL.test(secret)) return null;
  return { code, secret };
}

/** Decodes base64url (no padding) to bytes; `null` when it is not base64url. */
export function fromBase64Url(text: string): Uint8Array | null {
  if (!text || !BASE64URL.test(text)) return null;
  const base64 = text.replace(/-/g, '+').replace(/_/g, '/') + '==='.slice((text.length + 3) % 4);
  try {
    const binary = atob(base64);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    return bytes;
  } catch {
    return null;
  }
}

/** PBKDF2-HMAC-SHA256 over the UTF-8 bytes of `secret` (as printed), 32 bytes, like the server's `TicketSecrets.Derive`. */
export async function derivePbkdf2(secret: string, salt: Uint8Array, iterations: number, subtle: SubtleCrypto = globalThis.crypto.subtle): Promise<Uint8Array> {
  const key = await subtle.importKey('raw', new TextEncoder().encode(secret), 'PBKDF2', false, ['deriveBits']);
  const bits = await subtle.deriveBits({ name: 'PBKDF2', hash: 'SHA-256', salt: salt as BufferSource, iterations }, key, 256);
  return new Uint8Array(bits);
}

/** A list entry's verifier: salt and hash (base64url) and the iteration count. */
export interface TicketVerifier {
  salt: string;
  hash: string;
  iterations: number;
}

/**
 * Whether `secret` matches `verifier` (constant time over the hash). S8 measured 10.9 ms per verification at 50,000
 * iterations in WebCrypto, well inside a scan.
 */
export async function verifyTicketSecret(verifier: TicketVerifier, secret: string, subtle?: SubtleCrypto): Promise<boolean> {
  if (!secret || secret.length > 64 || verifier.iterations < 1) return false;
  const salt = fromBase64Url(verifier.salt);
  const expected = fromBase64Url(verifier.hash);
  if (!salt || !expected) return false;
  const actual = await derivePbkdf2(secret, salt, verifier.iterations, subtle);
  if (actual.length !== expected.length) return false;
  let diff = 0;
  for (let i = 0; i < actual.length; i++) diff |= actual[i] ^ expected[i];
  return diff === 0;
}
