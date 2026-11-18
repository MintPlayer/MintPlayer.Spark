import { parseWebhookSecret, readHeader, signWebhook, verifyWebhook, WebhookVerificationError } from './index';

// The Standard Webhooks specification's own test vector (standard-webhooks/standard-webhooks, the libraries' tests).
const vector = {
  secret: 'whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw',
  id: 'msg_p5jXN8AQM9LWM0D4loKWxJek',
  timestamp: 1614265330,
  body: '{"test": 2432232314}',
  signature: 'v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=',
};
const at = (seconds: number) => () => seconds * 1000;
const headers = (id: string, timestamp: string, signature: string) => ({ 'webhook-id': id, 'webhook-timestamp': timestamp, 'webhook-signature': signature });

async function failure(promise: Promise<unknown>): Promise<string> {
  try {
    await promise;
  } catch (error) {
    if (error instanceof WebhookVerificationError) return error.failure;
    throw error;
  }
  return 'none';
}

describe('verifyWebhook', () => {
  it('accepts the specification vector, as a string and as bytes', async () => {
    const options = { secrets: [vector.secret], now: at(vector.timestamp) };
    const h = headers(vector.id, String(vector.timestamp), vector.signature);
    await expect(verifyWebhook(h, vector.body, options)).resolves.toEqual({ messageId: vector.id, timestamp: vector.timestamp });
    await expect(verifyWebhook(h, new TextEncoder().encode(vector.body), options)).resolves.toEqual({ messageId: vector.id, timestamp: vector.timestamp });
  });

  it('signs what it verifies, and matches the specification vector', async () => {
    expect(await signWebhook(vector.secret, vector.id, vector.timestamp, vector.body)).toBe(vector.signature);
  });

  it('accepts any one of several signatures and secrets (rotation)', async () => {
    const other = 'whsec_' + btoa(String.fromCharCode(...new Uint8Array(32).fill(7)));
    const h = headers(vector.id, String(vector.timestamp), `v1a,abc= v1,AAAA ${vector.signature}`);
    await expect(verifyWebhook(h, vector.body, { secrets: [other, vector.secret], now: at(vector.timestamp) })).resolves.toBeTruthy();
  });

  it('refuses each broken part with its own failure', async () => {
    const options = { secrets: [vector.secret], now: at(vector.timestamp) };
    expect(await failure(verifyWebhook({ 'webhook-id': vector.id }, vector.body, options))).toBe('missing-headers');
    expect(await failure(verifyWebhook(headers(vector.id, '-5', vector.signature), vector.body, options))).toBe('invalid-timestamp');
    expect(await failure(verifyWebhook(headers(vector.id, '16142653.5', vector.signature), vector.body, options))).toBe('invalid-timestamp');
    expect(await failure(verifyWebhook(headers(vector.id, String(vector.timestamp), vector.signature), vector.body, { ...options, now: at(vector.timestamp + 301) }))).toBe('timestamp-out-of-tolerance');
    expect(await failure(verifyWebhook(headers(vector.id, String(vector.timestamp), vector.signature), vector.body + ' ', options))).toBe('invalid-signature');
    expect(await failure(verifyWebhook(headers('msg_other', String(vector.timestamp), vector.signature), vector.body, options))).toBe('invalid-signature');
    expect(await failure(verifyWebhook(headers(vector.id, String(vector.timestamp), 'v2,' + vector.signature.slice(3)), vector.body, options))).toBe('invalid-signature');
  });

  it('keeps the tolerance both ways and honours a custom one', async () => {
    const h = headers(vector.id, String(vector.timestamp), vector.signature);
    await expect(verifyWebhook(h, vector.body, { secrets: [vector.secret], now: at(vector.timestamp - 300) })).resolves.toBeTruthy();
    expect(await failure(verifyWebhook(h, vector.body, { secrets: [vector.secret], now: at(vector.timestamp - 301) }))).toBe('timestamp-out-of-tolerance');
    expect(await failure(verifyWebhook(h, vector.body, { secrets: [vector.secret], toleranceSeconds: 10, now: at(vector.timestamp + 11) }))).toBe('timestamp-out-of-tolerance');
  });

  it('fails closed without secrets and refuses a malformed secret', async () => {
    expect(() => verifyWebhook({}, '', { secrets: [] })).toThrow('at least one secret');
    expect(() => parseWebhookSecret('')).toThrow('required');
    expect(() => parseWebhookSecret('whsec_not base64!')).toThrow('base64');
    expect(() => parseWebhookSecret('whsec_' + btoa('short'))).toThrow('24 to 64 bytes');
    expect(parseWebhookSecret(vector.secret.slice('whsec_'.length))).toHaveLength(24);
    // A bad secret is a configuration error: it throws when the verifier is made, before any request.
    expect(() => verifyWebhook(headers(vector.id, String(vector.timestamp), vector.signature), vector.body, { secrets: ['whsec_bad'], now: at(vector.timestamp) })).toThrow('base64');
  });
});

describe('readHeader', () => {
  it('reads fetch Headers and plain objects case-insensitively', () => {
    expect(readHeader(new Headers({ 'Webhook-Id': 'a' }), 'webhook-id')).toBe('a');
    expect(readHeader({ 'Webhook-Id': 'b' }, 'webhook-id')).toBe('b');
    expect(readHeader({ 'x-many': ['1', '2'] }, 'X-Many')).toBe('1,2');
    expect(readHeader({}, 'webhook-id')).toBeUndefined();
  });
});
