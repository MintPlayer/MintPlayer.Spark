import {
  createWebhookVerifier,
  readHeader,
  WebhookBody,
  WebhookHeaderSource,
  WebhookVerificationError,
  WebhookVerificationFailure,
  WebhookVerifierOptions,
} from './verify';

/** The `ping` event a producer sends when an endpoint is created or changed. */
export const pingEventType = 'ping';

/** One verified delivery. */
export interface WebhookEvent<TType extends string = string, TData = unknown> {
  /** `webhook-id`: the same on every retry and redelivery. Deduplicate on it. */
  messageId: string;
  /** `webhook-timestamp` of this attempt (unix seconds). */
  attemptTimestamp: number;
  /** The event type, e.g. `order.shipped`. */
  type: TType;
  /** When the event happened (ISO 8601), not when this attempt was sent. */
  timestamp: string;
  data: TData;
  /** Any request header, e.g. the producer's own `webhook-installation-id`. */
  header(name: string): string | undefined;
}

/**
 * One handler per event type of `TEvents`, the event → data map `spark webhooks generate --lang ts` writes
 * (`ShopWebhookEvents`). Every handler is optional.
 */
export type WebhookHandlers<TEvents> = {
  [K in keyof TEvents & string]?: (data: TEvents[K], event: WebhookEvent<K, TEvents[K]>) => unknown;
};

export interface WebhookReceiverOptions<TEvents> extends WebhookVerifierOptions {
  /** The typed handlers. */
  on?: WebhookHandlers<TEvents>;
  /** Every event no typed handler took. Default: ignore it (a 2xx, so the producer does not retry). */
  onEvent?: (event: WebhookEvent) => unknown;
  /** The `ping` event. Default: acknowledge it. */
  onPing?: (event: WebhookEvent<'ping'>) => unknown;
}

/** 204 handled, 400 not a webhook envelope, 401 verification failed. */
export interface WebhookReceiveResult {
  status: 204 | 400 | 401;
  failure?: WebhookVerificationFailure;
  messageId?: string;
}

export interface WebhookReceiver {
  /**
   * Verifies, parses and dispatches one request. A handler's error propagates: answer it with 5xx and the producer
   * retries. Answer the result's `status` otherwise.
   */
  receive(headers: WebhookHeaderSource, body: WebhookBody): Promise<WebhookReceiveResult>;
  /** {@link receive} for a fetch-style host (Cloudflare Workers, Deno, Bun, Next.js route handlers, Hono). */
  fetch(request: Request): Promise<Response>;
}

/**
 * The whole receive path: verify the Standard Webhooks signature, parse the envelope, call the typed handler.
 *
 * ```ts
 * const receiver = createWebhookReceiver<ShopWebhookEvents>({
 *   secrets: [process.env.SHOP_WEBHOOK_SECRET!],
 *   on: { 'order.shipped': async (order, event) => ship(order.orderId, event.messageId) },
 * });
 * export const POST = (request: Request) => receiver.fetch(request);
 * ```
 */
export function createWebhookReceiver<TEvents extends object>(options: WebhookReceiverOptions<TEvents>): WebhookReceiver {
  const verify = createWebhookVerifier(options);
  const handlers = (options.on ?? {}) as Record<string, ((data: unknown, event: WebhookEvent) => unknown) | undefined>;

  const receive = async (headers: WebhookHeaderSource, body: WebhookBody): Promise<WebhookReceiveResult> => {
    let verified;
    try {
      verified = await verify(headers, body);
    } catch (error) {
      if (error instanceof WebhookVerificationError) return { status: 401, failure: error.failure, messageId: error.messageId };
      throw error;
    }

    const envelope = parseEnvelope(body);
    if (!envelope) return { status: 400, messageId: verified.messageId };

    const event: WebhookEvent = {
      messageId: verified.messageId,
      attemptTimestamp: verified.timestamp,
      type: envelope.type,
      timestamp: envelope.timestamp,
      data: envelope.data,
      header: (name) => readHeader(headers, name),
    };
    const handler = Object.prototype.hasOwnProperty.call(handlers, event.type) ? handlers[event.type] : undefined;
    if (event.type === pingEventType) await options.onPing?.(event as WebhookEvent<'ping'>);
    else if (handler) await handler(event.data, event);
    else await options.onEvent?.(event);
    return { status: 204, messageId: verified.messageId };
  };

  return {
    receive,
    async fetch(request) {
      const result = await receive(request.headers, new Uint8Array(await request.arrayBuffer()));
      return new Response(null, { status: result.status });
    },
  };
}

function parseEnvelope(body: WebhookBody): { type: string; timestamp: string; data: unknown } | undefined {
  try {
    const text = typeof body === 'string' ? body : new TextDecoder().decode(body);
    const value = JSON.parse(text) as unknown;
    if (typeof value !== 'object' || value === null || Array.isArray(value)) return undefined;
    const { type, timestamp, data } = value as Record<string, unknown>;
    if (typeof type !== 'string' || !type) return undefined;
    return { type, timestamp: typeof timestamp === 'string' ? timestamp : '', data };
  } catch {
    return undefined;
  }
}
