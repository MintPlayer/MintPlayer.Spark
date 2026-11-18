/**
 * Standard Webhooks verification (https://www.standardwebhooks.com) on WebCrypto, so it runs on Node 20+, Deno, Bun,
 * Cloudflare Workers and other edge runtimes. The rules match MintPlayer.Webhooks.Receiver (.NET): every header
 * present, `webhook-timestamp` whole unix seconds within the tolerance, and one `v1` signature that is the
 * HMAC-SHA256 of `{webhook-id}.{webhook-timestamp}.{body}` under one of the secrets.
 */

/** The Standard Webhooks header names. */
export const webhookHeaders = {
  /** The message id: unchanged across retries and redeliveries, so it is the receiver's idempotency key. */
  id: 'webhook-id',
  /** Unix seconds of this attempt. Signed, so a captured request cannot be replayed outside the tolerance. */
  timestamp: 'webhook-timestamp',
  /** Space-separated `version,base64` signatures (more than one while a secret rotates). */
  signature: 'webhook-signature',
} as const;

export type WebhookVerificationFailure = 'missing-headers' | 'invalid-timestamp' | 'timestamp-out-of-tolerance' | 'invalid-signature';

/** Thrown by {@link verifyWebhook}. Answer it with 401: the signature is the authentication. */
export class WebhookVerificationError extends Error {
  constructor(
    readonly failure: WebhookVerificationFailure,
    readonly messageId?: string,
  ) {
    super(`Webhook verification failed: ${failure}.`);
    this.name = 'WebhookVerificationError';
  }
}

/** Request headers: a fetch `Headers`, or a plain object such as Node's `IncomingHttpHeaders` (lower-case names). */
export type WebhookHeaderSource = Headers | Record<string, string | string[] | undefined>;

/** The raw request body. Pass the bytes as received: a re-serialized JSON body no longer matches its signature. */
export type WebhookBody = string | Uint8Array | ArrayBuffer;

export interface WebhookVerifierOptions {
  /** `whsec_` secrets accepted for `v1`. Keep the old one here while a secret rotates. */
  secrets: readonly string[];
  /** How far `webhook-timestamp` may be from now, either way. Default 300 (5 minutes). */
  toleranceSeconds?: number;
  /** The clock, in milliseconds since the epoch. Tests replace it. */
  now?: () => number;
}

export interface VerifiedWebhook {
  /** `webhook-id`: deduplicate on it. */
  messageId: string;
  /** `webhook-timestamp` of this attempt (unix seconds). */
  timestamp: number;
}

export const webhookSecretPrefix = 'whsec_';
const minKeyBytes = 24;
const maxKeyBytes = 64;
const encoder = new TextEncoder();

/** The HMAC key of a `whsec_<base64>` secret (the prefix is optional). Throws on anything but 24 to 64 bytes of base64. */
export function parseWebhookSecret(secret: string): Uint8Array {
  let encoded = (secret ?? '').trim();
  if (!encoded) throw new Error('A webhook secret is required.');
  if (encoded.startsWith(webhookSecretPrefix)) encoded = encoded.slice(webhookSecretPrefix.length);
  const key = fromBase64(encoded);
  if (!key) throw new Error('A webhook secret is base64 after the whsec_ prefix.');
  if (key.length < minKeyBytes || key.length > maxKeyBytes)
    throw new Error(`A webhook secret is ${minKeyBytes} to ${maxKeyBytes} bytes; this one is ${key.length}.`);
  return key;
}

/** Reads one header, case-insensitively; a repeated header is joined with commas. */
export function readHeader(headers: WebhookHeaderSource, name: string): string | undefined {
  if (typeof Headers !== 'undefined' && headers instanceof Headers) return headers.get(name) ?? undefined;
  const record = headers as Record<string, string | string[] | undefined>;
  const lower = name.toLowerCase();
  const key = lower in record ? lower : Object.keys(record).find((k) => k.toLowerCase() === lower);
  const value = key === undefined ? undefined : record[key];
  return Array.isArray(value) ? value.join(',') : value;
}

/**
 * Builds a verifier for these options. The secrets are parsed and imported once; a verifier with none refuses to
 * exist (fail closed: nothing to verify against is a configuration error, never "accept all").
 */
export function createWebhookVerifier(options: WebhookVerifierOptions): (headers: WebhookHeaderSource, body: WebhookBody) => Promise<VerifiedWebhook> {
  if (!options?.secrets?.length) throw new Error('A webhook receiver needs at least one secret.');
  // Parsed now, so a malformed secret throws here, at startup, not on the first request.
  const parsed = options.secrets.map(parseWebhookSecret);
  const keys = Promise.all(parsed.map((key) => importKey(key, 'verify')));
  // A failed import surfaces on the first request that awaits it, not as an unhandled rejection at startup.
  keys.catch(() => undefined);
  const tolerance = options.toleranceSeconds ?? 300;
  const now = options.now ?? Date.now;

  return async (headers, body) => {
    const id = readHeader(headers, webhookHeaders.id);
    const timestampText = readHeader(headers, webhookHeaders.timestamp);
    const signatureText = readHeader(headers, webhookHeaders.signature);
    if (!id || !timestampText || !signatureText) throw new WebhookVerificationError('missing-headers');

    if (!/^\d+$/.test(timestampText)) throw new WebhookVerificationError('invalid-timestamp', id);
    const timestamp = Number(timestampText);
    if (!Number.isSafeInteger(timestamp)) throw new WebhookVerificationError('invalid-timestamp', id);
    if (Math.abs(Math.floor(now() / 1000) - timestamp) > tolerance) throw new WebhookVerificationError('timestamp-out-of-tolerance', id);

    const signed = concat(encoder.encode(`${id}.${timestamp}.`), toBytes(body));
    const imported = await keys;
    for (const signature of parseSignatures(signatureText)) {
      if (signature.version !== 'v1') continue;
      for (const key of imported) {
        // subtle.verify compares in constant time.
        if (await crypto.subtle.verify('HMAC', key, signature.bytes as BufferSource, signed as BufferSource)) return { messageId: id, timestamp };
      }
    }
    throw new WebhookVerificationError('invalid-signature', id);
  };
}

/** Verifies one request. To verify many, keep the function {@link createWebhookVerifier} returns. */
export function verifyWebhook(headers: WebhookHeaderSource, body: WebhookBody, options: WebhookVerifierOptions): Promise<VerifiedWebhook> {
  return createWebhookVerifier(options)(headers, body);
}

/** The `webhook-signature` value (`v1,<base64>`) for a request; for a receiver's own tests. */
export async function signWebhook(secret: string, messageId: string, timestamp: number, body: WebhookBody): Promise<string> {
  const key = await importKey(parseWebhookSecret(secret), 'sign');
  const signature = await crypto.subtle.sign('HMAC', key, concat(encoder.encode(`${messageId}.${timestamp}.`), toBytes(body)) as BufferSource);
  return 'v1,' + toBase64(new Uint8Array(signature));
}

function importKey(key: Uint8Array, usage: 'sign' | 'verify'): Promise<CryptoKey> {
  return crypto.subtle.importKey('raw', key as BufferSource, { name: 'HMAC', hash: 'SHA-256' }, false, [usage]);
}

function parseSignatures(header: string): { version: string; bytes: Uint8Array }[] {
  const result: { version: string; bytes: Uint8Array }[] = [];
  for (const token of header.split(' ')) {
    const comma = token.indexOf(',');
    if (comma <= 0 || comma === token.length - 1) continue;
    const bytes = fromBase64(token.slice(comma + 1));
    // Not base64: ignore this entry, as the .NET receiver does.
    if (bytes) result.push({ version: token.slice(0, comma), bytes });
  }
  return result;
}

function toBytes(body: WebhookBody): Uint8Array {
  if (typeof body === 'string') return encoder.encode(body);
  return body instanceof Uint8Array ? body : new Uint8Array(body);
}

function concat(a: Uint8Array, b: Uint8Array): Uint8Array {
  const result = new Uint8Array(a.length + b.length);
  result.set(a, 0);
  result.set(b, a.length);
  return result;
}

function fromBase64(text: string): Uint8Array | undefined {
  if (!/^[A-Za-z0-9+/]*={0,2}$/.test(text) || text.length % 4 !== 0) return undefined;
  try {
    const binary = atob(text);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    return bytes;
  } catch {
    return undefined;
  }
}

function toBase64(bytes: Uint8Array): string {
  let binary = '';
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary);
}
