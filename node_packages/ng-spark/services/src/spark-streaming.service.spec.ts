import { TestBed } from '@angular/core/testing';
import { describe, expect, it, beforeEach, afterEach, vi } from 'vitest';
import { firstValueFrom, take, toArray } from 'rxjs';

import { SparkStreamingService } from './spark-streaming.service';
import { SPARK_CONFIG } from '@mintplayer/ng-spark';

/**
 * Drives the WebSocket lifecycle in SparkStreamingService — connect, message
 * delivery, normal-closure complete, and exponential-backoff reconnect on
 * abnormal closure. The native WebSocket is replaced with a controllable mock
 * so we can drive each transition synchronously.
 */
describe('SparkStreamingService', () => {
  let createdSockets: MockWebSocket[];
  let originalWebSocket: typeof WebSocket;

  // Minimal mock that mirrors only the API surface SparkStreamingService uses.
  class MockWebSocket {
    static OPEN = 1;
    static CONNECTING = 0;
    static CLOSED = 3;

    readyState = MockWebSocket.CONNECTING;
    url: string;
    onopen: ((ev: Event) => void) | null = null;
    onmessage: ((ev: MessageEvent) => void) | null = null;
    onerror: ((ev: Event) => void) | null = null;
    onclose: ((ev: CloseEvent) => void) | null = null;
    closeCalls: { code?: number; reason?: string }[] = [];

    constructor(url: string) {
      this.url = url;
      createdSockets.push(this);
    }

    open() {
      this.readyState = MockWebSocket.OPEN;
      this.onopen?.(new Event('open'));
    }

    receive(data: string) {
      this.onmessage?.({ data } as MessageEvent);
    }

    fireClose(code: number) {
      this.readyState = MockWebSocket.CLOSED;
      this.onclose?.({ code } as CloseEvent);
    }

    close(code?: number, reason?: string) {
      this.closeCalls.push({ code, reason });
      this.readyState = MockWebSocket.CLOSED;
    }
  }

  beforeEach(() => {
    createdSockets = [];
    originalWebSocket = (globalThis as any).WebSocket;
    (globalThis as any).WebSocket = MockWebSocket;
    vi.useFakeTimers();
  });

  afterEach(() => {
    (globalThis as any).WebSocket = originalWebSocket;
    vi.useRealTimers();
  });

  function configure(baseUrl: string | undefined = undefined) {
    TestBed.configureTestingModule({
      providers: baseUrl
        ? [{ provide: SPARK_CONFIG, useValue: { baseUrl } }]
        : [],
    });
    return TestBed.inject(SparkStreamingService);
  }

  it('opens a websocket using ws:// for an http baseUrl', () => {
    const service = configure('http://api.example.com/spark');

    const sub = service.connectToStreamingQuery('q/1').subscribe();
    expect(createdSockets[0].url).toBe('ws://api.example.com/spark/queries/q%2F1/stream');

    sub.unsubscribe();
  });

  it('opens a websocket using wss:// for an https baseUrl', () => {
    const service = configure('https://api.example.com/spark');

    const sub = service.connectToStreamingQuery('q/2').subscribe();
    expect(createdSockets[0].url).toBe('wss://api.example.com/spark/queries/q%2F2/stream');

    sub.unsubscribe();
  });

  it('emits parsed messages received from the socket on the consumer subscription', async () => {
    const service = configure('http://api.example.com/spark');

    const promise = firstValueFrom(service.connectToStreamingQuery('q').pipe(take(2), toArray()));

    const ws = createdSockets[0];
    ws.open();
    ws.receive(JSON.stringify({ type: 'snapshot', data: [{ id: 'p/1' }] }));
    ws.receive(JSON.stringify({ type: 'patch', updated: [] }));

    await expect(promise).resolves.toEqual([
      { type: 'snapshot', data: [{ id: 'p/1' }] },
      { type: 'patch', updated: [] },
    ]);
  });

  it('ignores malformed JSON without erroring the subscription', () => {
    const service = configure('http://api.example.com/spark');
    const next = vi.fn();
    const error = vi.fn();

    service.connectToStreamingQuery('q').subscribe({ next, error });
    const ws = createdSockets[0];
    ws.open();
    ws.receive('not-json');

    expect(next).not.toHaveBeenCalled();
    expect(error).not.toHaveBeenCalled();
  });

  it('completes the subscription on normal closure (code 1000) and does not reconnect', () => {
    const service = configure('http://api.example.com/spark');
    const complete = vi.fn();

    service.connectToStreamingQuery('q').subscribe({ complete });
    const ws = createdSockets[0];
    ws.open();
    ws.fireClose(1000);
    vi.advanceTimersByTime(60_000);

    expect(complete).toHaveBeenCalledOnce();
    expect(createdSockets).toHaveLength(1); // no reconnect attempt
  });

  it('reconnects with exponential backoff on abnormal closure', () => {
    const service = configure('http://api.example.com/spark');
    service.connectToStreamingQuery('q').subscribe();

    const first = createdSockets[0];
    first.open();
    first.fireClose(1006); // abnormal

    // First retry is at 1000ms (2^0 * 1000)
    vi.advanceTimersByTime(1000);
    expect(createdSockets).toHaveLength(2);

    const second = createdSockets[1];
    second.fireClose(1006);

    // Second retry is at 2000ms (2^1 * 1000)
    vi.advanceTimersByTime(2000);
    expect(createdSockets).toHaveLength(3);
  });

  it('closes the socket cleanly on unsubscribe', () => {
    const service = configure('http://api.example.com/spark');
    const sub = service.connectToStreamingQuery('q').subscribe();
    const ws = createdSockets[0];
    ws.open();

    sub.unsubscribe();

    expect(ws.closeCalls).toEqual([{ code: 1000, reason: 'Client unsubscribed' }]);
  });
});
