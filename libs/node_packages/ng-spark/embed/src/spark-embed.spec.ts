import { DOCUMENT } from '@angular/common';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { SPARK_EMBED_MESSAGES, sparkEmbedOriginAllowed } from './spark-embed-origin';
import { SparkEmbedService } from './spark-embed.service';

// Commerce #521 X23 (GB8): embed mode inside the platform's embed routes.

describe('embed: origin rules', () => {
  const allowed = ['https://organiser.example', 'https://*.festival.example', 'http://localhost:4200'];

  it('matches exact origins and subdomains of a wildcard, never the bare wildcard domain', () => {
    expect(sparkEmbedOriginAllowed('https://organiser.example', allowed)).toBe(true);
    expect(sparkEmbedOriginAllowed('https://shop.festival.example', allowed)).toBe(true);
    expect(sparkEmbedOriginAllowed('https://festival.example', allowed)).toBe(false);
    expect(sparkEmbedOriginAllowed('http://localhost:4200', allowed)).toBe(true);
  });

  it('refuses other schemes, ports, look-alike hosts and anything that is not an origin', () => {
    expect(sparkEmbedOriginAllowed('http://organiser.example', allowed)).toBe(false);
    expect(sparkEmbedOriginAllowed('https://organiser.example:8443', allowed)).toBe(false);
    expect(sparkEmbedOriginAllowed('https://organiser.example.evil.test', allowed)).toBe(false);
    expect(sparkEmbedOriginAllowed('https://evilfestival.example', allowed)).toBe(false);
    expect(sparkEmbedOriginAllowed('https://organiser.example/page', allowed)).toBe(false);
    expect(sparkEmbedOriginAllowed(null, allowed)).toBe(false);
  });
});

describe('embed: hand-over', () => {
  /** A framed window: `top` differs from `self`, and touching `top.location` throws (cross-origin, sandboxed). */
  function framedWindow(options: { popup: boolean; ancestor: string | null }) {
    const popup = options.popup ? { opener: {}, location: { href: '' }, close: vi.fn() } : null;
    const parent = { postMessage: vi.fn() };
    const self: Record<string, unknown> = {
      open: vi.fn(() => popup),
      parent,
      location: { search: '', ancestorOrigins: options.ancestor ? [options.ancestor] : [], assign: vi.fn() },
    };
    self['self'] = self;
    Object.defineProperty(self, 'top', {
      get: () => ({ get location() { throw new Error('cross-origin'); } }),
    });
    return { window: self, popup, parent };
  }

  function setup(win: unknown) {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: DOCUMENT, useValue: { defaultView: win, referrer: '', documentElement: { scrollHeight: 100 } } },
      ],
    });
    return { embed: TestBed.inject(SparkEmbedService), http: TestBed.inject(HttpTestingController) };
  }

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('opens the popup inside the click, then points it at the signed URL', async () => {
    const framed = framedWindow({ popup: true, ancestor: 'https://organiser.example' });
    const { embed, http } = setup(framed.window);

    const result = embed.handOver({ path: '/checkout', payload: { offers: ['o1'] } });
    expect(framed.window['open']).toHaveBeenCalledWith('', '_blank');
    expect(framed.popup!.opener).toBeNull();
    const request = http.expectOne('/spark/embed/handover');
    expect(request.request.body).toEqual({ path: '/checkout', payload: { offers: ['o1'] } });
    request.flush({ url: 'https://tickets.example/checkout?spark-handover=t', expiresAt: '2026-10-19T10:15:00Z' });

    expect(await result).toEqual({ mode: 'popup', url: 'https://tickets.example/checkout?spark-handover=t' });
    expect(framed.popup!.location.href).toBe('https://tickets.example/checkout?spark-handover=t');
  });

  it('asks the loader to navigate the organiser page when the popup is blocked, to an allowed origin only', async () => {
    const framed = framedWindow({ popup: false, ancestor: 'https://organiser.example' });
    const { embed, http } = setup(framed.window);

    const result = embed.handOver({ path: '/checkout' });
    http.expectOne('/spark/embed/handover').flush({ url: 'https://tickets.example/checkout?spark-handover=t', expiresAt: '' });
    await vi.waitFor(() => http.expectOne('/spark/embed/config').flush({ allowedOrigins: ['https://organiser.example'], pathPrefixes: ['/embed'] }));

    expect((await result).mode).toBe('parent');
    expect(framed.parent.postMessage).toHaveBeenCalledWith(
      { type: SPARK_EMBED_MESSAGES.navigate, url: 'https://tickets.example/checkout?spark-handover=t' },
      'https://organiser.example');
  });

  it('falls back to a link when the embedding origin is not allowed and the top window cannot be navigated', async () => {
    const framed = framedWindow({ popup: false, ancestor: 'https://evil.example' });
    const { embed, http } = setup(framed.window);

    const result = embed.handOver({ path: '/queue' }, 'top');
    expect(framed.window['open']).not.toHaveBeenCalled();
    http.expectOne('/spark/embed/handover').flush({ url: 'https://tickets.example/queue?spark-handover=t', expiresAt: '' });
    await vi.waitFor(() => http.expectOne('/spark/embed/config').flush({ allowedOrigins: ['https://organiser.example'], pathPrefixes: ['/embed'] }));

    expect((await result).mode).toBe('link');
    expect(framed.parent.postMessage).not.toHaveBeenCalled();
    expect(embed.pendingLink()).toBe('https://tickets.example/queue?spark-handover=t');
  });
});
