import { webcrypto } from 'node:crypto';
import { describe, expect, it } from 'vitest';
import { SparkMemoryStore } from '@mintplayer/ng-spark/offline';
import {
  SparkScannerEntry,
  SparkScannerListPage,
  SparkScannerSession,
  SparkScannerSyncRequest,
  SparkScannerSyncResult,
  SparkScanResult,
} from '../generated/contracts';
import { derivePbkdf2 } from '../ticket-secrets';
import { TicketScannerApi, TicketScannerApiError, TicketScannerEngine, TicketScannerStorage } from './ticket-scanner';

// Commerce #521 M15 (S9): the door scanner's engine against a fake server and memory storage. The server half of S9
// (paging, double entries across devices) is OfflineListSpikeTests / TicketCheckInFlowTests in .NET.

const subtle = webcrypto.subtle as unknown as SubtleCrypto;
const NOW = Date.parse('2026-07-01T17:30:00Z');
const CODE = '0123456789ABCDEF';
const SECRET = 'abcdefghijklmnopqrstuv';

function b64url(bytes: Uint8Array): string {
  return Buffer.from(bytes).toString('base64').replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_');
}

async function entryFor(code: string, secret: string, overrides: Partial<SparkScannerEntry> = {}): Promise<SparkScannerEntry> {
  const salt = new Uint8Array(16).map((_, i) => i + 1);
  const hash = await derivePbkdf2(secret, salt, 100, subtle);
  return { code, salt: b64url(salt), hash: b64url(hash), iterations: 100, ticketTypeId: 'TicketTypes/standard', dateIds: ['D1'], status: 'Valid', inside: [], used: [], ...overrides };
}

function session(overrides: Partial<SparkScannerSession> = {}): SparkScannerSession {
  return {
    deviceId: 'TicketScannerDevices/a', name: 'Gate A', eventId: 'TicketEvents/1', eventName: { en: 'Fest' } as never,
    list: { id: 'L', name: 'Gate', kind: 'Gate', dateIds: [], ticketTypeIds: [], allowReentry: false },
    dates: [{ id: 'D1', startsAt: '2026-07-01T18:00:00Z', doorsOpenAt: '2026-07-01T17:00:00Z', endsAt: '2026-07-02T00:00:00Z' }],
    dateId: null, iterations: 100, freezeAt: null, wipeAfter: '2026-07-02T12:00:00Z', serverTime: new Date(NOW).toISOString(), ticketTypes: [],
    ...overrides,
  };
}

class FakeServer implements TicketScannerApi {
  online = true;
  revoked = false;
  pages: SparkScannerEntry[][] = [];
  synced: SparkScannerSyncRequest[] = [];
  doubleEntries: string[] = [];
  changes: SparkScannerEntry[] = [];
  current = session();

  private check(): void {
    if (this.revoked) throw new TicketScannerApiError('revoked', 'deviceRevoked');
    if (!this.online) throw new TicketScannerApiError('offline');
  }

  async session(): Promise<SparkScannerSession> {
    this.check();
    return this.current;
  }

  async listPage(next: string | null): Promise<SparkScannerListPage> {
    this.check();
    const index = next ? Number(next) : 0;
    return { entries: this.pages[index] ?? [], next: index + 1 < this.pages.length ? String(index + 1) : null, cursor: 'c' + index, serverTime: '' };
  }

  async scan(): Promise<SparkScanResult> {
    this.check();
    return { scanId: 'x', result: 'admitted', code: CODE, doubleEntry: false, dateId: 'D1' };
  }

  async sync(request: SparkScannerSyncRequest): Promise<SparkScannerSyncResult> {
    this.check();
    this.synced.push(request);
    return { results: [], doubleEntries: this.doubleEntries, changes: this.changes, cursor: 'c-after', serverTime: '' };
  }
}

function storage(): TicketScannerStorage {
  return { list: new SparkMemoryStore(), queue: new SparkMemoryStore(), meta: new SparkMemoryStore() };
}

describe('TicketScannerEngine', () => {
  it('checks a scan against the offline list and queues it when the server cannot be reached', async () => {
    const server = new FakeServer();
    server.pages = [[await entryFor(CODE, SECRET)]];
    const store = storage();
    const engine = new TicketScannerEngine(server, store, () => NOW, subtle);
    await engine.start();
    expect(await engine.download()).toBe(1);

    server.online = false;
    const first = await engine.scan(`ST1.${CODE}.${SECRET}`);
    expect(first).toMatchObject({ result: 'admitted', offline: true, doubleEntry: false });
    const second = await engine.scan(`ST1.${CODE}.${SECRET}`);
    expect(second).toMatchObject({ result: 'alreadyIn', offline: true, doubleEntry: true });
    // A wrong secret is unknown and leaves no trace.
    expect((await engine.scan(`ST1.${CODE}.zzzzzzzzzzzzzzzzzzzzzz`)).result).toBe('unknown');
    expect((await engine.status()).queued).toBe(2);
  });

  it('syncs the queue, learns double entries and applies the list changes', async () => {
    const server = new FakeServer();
    server.pages = [[await entryFor(CODE, SECRET)]];
    const store = storage();
    const engine = new TicketScannerEngine(server, store, () => NOW, subtle);
    await engine.start();
    await engine.download();
    server.online = false;
    await engine.scan(`ST1.${CODE}.${SECRET}`);

    server.online = true;
    server.doubleEntries = ['other-device-scan'];
    server.changes = [await entryFor(CODE, SECRET, { status: 'Revoked' })];
    expect(await engine.sync()).toBe(1);

    expect(server.synced[0].scans).toHaveLength(1);
    expect(server.synced[0].scans[0]).toMatchObject({ code: CODE, direction: 'In', dateId: 'D1' });
    expect(server.synced[0].since).toBe('c0');
    const status = await engine.status();
    expect(status.queued).toBe(0);
    expect(status.doubleEntries).toEqual(['other-device-scan']);
    // The revocation reached the device: offline, the ticket is now refused.
    server.online = false;
    expect((await engine.scan(`ST1.${CODE}.${SECRET}`)).result).toBe('revoked');
  });

  it('keeps the queue when a sync fails', async () => {
    const server = new FakeServer();
    server.pages = [[await entryFor(CODE, SECRET)]];
    const engine = new TicketScannerEngine(server, storage(), () => NOW, subtle);
    await engine.start();
    await engine.download();
    server.online = false;
    await engine.scan(`ST1.${CODE}.${SECRET}`);
    await expect(engine.sync()).rejects.toBeInstanceOf(TicketScannerApiError);
    expect((await engine.status()).queued).toBe(1);
  });

  it('wipes everything when the device is revoked', async () => {
    const server = new FakeServer();
    server.pages = [[await entryFor(CODE, SECRET)]];
    const store = storage();
    const engine = new TicketScannerEngine(server, store, () => NOW, subtle);
    await engine.start();
    await engine.download();
    server.online = false;
    await engine.scan(`ST1.${CODE}.${SECRET}`);

    server.online = true;
    server.revoked = true;
    await expect(engine.sync()).rejects.toMatchObject({ kind: 'revoked' });
    expect(engine.current).toBeNull();
    expect(await store.list.count()).toBe(0);
    expect(await store.queue.count()).toBe(0);
    expect(await store.meta.count()).toBe(0);
  });

  it('wipes everything after the event (WipeAfter), even offline', async () => {
    const server = new FakeServer();
    const store = storage();
    await new TicketScannerEngine(server, store, () => NOW, subtle).start();
    await store.list.put(CODE, await entryFor(CODE, SECRET));

    server.online = false;
    const late = new TicketScannerEngine(server, store, () => Date.parse('2026-07-02T12:00:01Z'), subtle);
    expect(await late.start()).toBeNull();
    expect(await store.list.count()).toBe(0);
  });

  it('S9: stores a 50,000-ticket list in pages and wipes it', async () => {
    const server = new FakeServer();
    const template = await entryFor(CODE, SECRET);
    server.pages = Array.from({ length: 10 }, (_, p) =>
      Array.from({ length: 5_000 }, (_, i) => ({ ...template, code: (p * 5_000 + i).toString(32).toUpperCase().padStart(16, '0') })));
    const store = storage();
    const engine = new TicketScannerEngine(server, store, () => NOW, subtle);
    await engine.start();

    expect(await engine.download()).toBe(50_000);
    await engine.wipe();
    expect(await store.list.count()).toBe(0);
  });
});
