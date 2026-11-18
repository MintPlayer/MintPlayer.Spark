import { createWebhookReceiver, signWebhook, WebhookEvent } from './index';
// What `spark webhooks generate --lang ts` writes for the test site's catalog (kept current by the .NET golden test,
// WebhookReceiverRoundTripTests), so these specs type-check against real generated output.
import { BuildFinished, OrderShipped, ShopWebhookEvents, shopWebhookEventTypes } from './testing/shop-webhooks';

const secret = 'whsec_' + btoa(String.fromCharCode(...new Uint8Array(32).map((_, i) => i)));
const now = 1_763_460_000;

async function request(type: string, data: unknown, id = 'msg_1', key = secret): Promise<Request> {
  const body = JSON.stringify({ type, timestamp: '2026-11-18T10:00:00Z', data });
  return new Request('https://receiver.example.com/webhooks/shop', {
    method: 'POST',
    headers: {
      'content-type': 'application/json',
      'webhook-id': id,
      'webhook-timestamp': String(now),
      'webhook-signature': await signWebhook(key, id, now, body),
      'webhook-installation-id': 'inst_9',
    },
    body,
  });
}

describe('createWebhookReceiver', () => {
  it('dispatches a verified event to its typed handler', async () => {
    const shipped: { data: OrderShipped; event: WebhookEvent<'order.shipped', OrderShipped> }[] = [];
    const receiver = createWebhookReceiver<ShopWebhookEvents>({
      secrets: [secret],
      now: () => now * 1000,
      on: { 'order.shipped': (data, event) => void shipped.push({ data, event }) },
    });

    const response = await receiver.fetch(await request('order.shipped', { orderId: 'o-1', items: 3, carrier: null }));

    expect(response.status).toBe(204);
    expect(shipped).toHaveLength(1);
    expect(shipped[0].data).toEqual({ orderId: 'o-1', items: 3, carrier: null });
    expect(shipped[0].event.messageId).toBe('msg_1');
    expect(shipped[0].event.attemptTimestamp).toBe(now);
    expect(shipped[0].event.timestamp).toBe('2026-11-18T10:00:00Z');
    expect(shipped[0].event.header('Webhook-Installation-Id')).toBe('inst_9');
  });

  it('carries nested, enum and recursive data as sent', async () => {
    let received: BuildFinished | undefined;
    const receiver = createWebhookReceiver<ShopWebhookEvents>({ secrets: [secret], now: () => now * 1000, on: { 'build.finished': (data) => void (received = data) } });
    const leaf = { name: 'test', durationMs: 900, children: [] };
    const data: BuildFinished = {
      buildId: 'b-7', outcome: 'timedOut', previousOutcome: null, commit: { sha: 'abc', message: null },
      steps: [{ name: 'build', durationMs: 1200, children: [leaf] }], coverage: { src: 81.25 }, runId: '00000000-0000-0000-0000-000000000001', slowest: leaf,
    };

    expect((await receiver.fetch(await request('build.finished', data))).status).toBe(204);
    expect(received).toEqual(data);
  });

  it('acknowledges ping and unknown events, calling their optional handlers', async () => {
    const seen: string[] = [];
    const receiver = createWebhookReceiver<ShopWebhookEvents>({
      secrets: [secret],
      now: () => now * 1000,
      onPing: (event) => void seen.push('ping:' + event.messageId),
      onEvent: (event) => void seen.push('other:' + event.type),
    });

    expect((await receiver.fetch(await request('ping', {}, 'msg_p'))).status).toBe(204);
    expect((await receiver.fetch(await request('shop.closed', {}))).status).toBe(204);
    // A declared event without a handler falls through to onEvent too.
    expect((await receiver.fetch(await request('invoice.paid', { invoiceId: 'i', amount: 1, paidAt: '2026-11-18T10:00:00Z' }))).status).toBe(204);
    expect(seen).toEqual(['ping:msg_p', 'other:shop.closed', 'other:invoice.paid']);
    // Without handlers nothing is required: the event is acknowledged so the producer does not retry it forever.
    const bare = createWebhookReceiver<ShopWebhookEvents>({ secrets: [secret], now: () => now * 1000 });
    expect((await bare.fetch(await request('order.shipped', {}))).status).toBe(204);
  });

  it('answers 401 for a wrong signature and 400 for a signed body that is not an envelope', async () => {
    const handled: string[] = [];
    const receiver = createWebhookReceiver<ShopWebhookEvents>({ secrets: [secret], now: () => now * 1000, on: { 'order.shipped': () => void handled.push('x') } });
    const other = 'whsec_' + btoa(String.fromCharCode(...new Uint8Array(32).fill(1)));

    const forged = await receiver.receive(Object.fromEntries((await request('order.shipped', {}, 'msg_f', other)).headers), JSON.stringify({ type: 'order.shipped', data: {} }));
    expect(forged).toEqual({ status: 401, failure: 'invalid-signature', messageId: 'msg_f' });

    for (const body of ['not json', '[]', '{"data":{}}', '{"type":""}']) {
      const signature = await signWebhook(secret, 'msg_b', now, body);
      const result = await receiver.receive({ 'webhook-id': 'msg_b', 'webhook-timestamp': String(now), 'webhook-signature': signature }, body);
      expect(result).toEqual({ status: 400, messageId: 'msg_b' });
    }
    expect(handled).toEqual([]);
  });

  it('lets a handler error propagate, so the host answers 5xx and the producer retries', async () => {
    const receiver = createWebhookReceiver<ShopWebhookEvents>({
      secrets: [secret],
      now: () => now * 1000,
      on: { 'order.shipped': async () => { throw new Error('database down'); } },
    });
    await expect(receiver.fetch(await request('order.shipped', {}))).rejects.toThrow('database down');
  });

  it('never treats an inherited property name as a handler', async () => {
    const receiver = createWebhookReceiver<ShopWebhookEvents>({ secrets: [secret], now: () => now * 1000 });
    expect((await receiver.fetch(await request('constructor', {}))).status).toBe(204);
    expect((await receiver.fetch(await request('__proto__', {}))).status).toBe(204);
  });

  it('lists the generated event types', () => {
    expect(shopWebhookEventTypes).toEqual(['build.finished', 'invoice.paid', 'order.shipped']);
  });
});
