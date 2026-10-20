// The door scanner's engine (commerce PRD §4.11 "Scanner PWA", S9), framework-free so it runs under test with fakes:
// online scans go to the server (atomic per check-in list); without a connection a scan is verified against the
// downloaded PBKDF2 list with the same rules (check-in-rules.ts) and queued; a sync uploads the queue, learns the double
// entries every device made and the list's changes; a revoked device, or one past its wipe time, deletes everything.

import { SparkOfflineCommand, SparkOfflineQueue, SparkOfflineStore } from '@mintplayer/ng-spark/offline';
import {
  SparkOfflineScan,
  SparkScannerEntry,
  SparkScannerListPage,
  SparkScannerSession,
  SparkScannerSyncRequest,
  SparkScannerSyncResult,
  SparkScanRequest,
  SparkScanResult,
} from '../generated/contracts';
import { applyOfflineScan, checkInDateFor, TicketScanDirection, TicketScanResult } from '../check-in-rules';
import { parseTicketPayload, verifyTicketSecret } from '../ticket-secrets';

/** Why a scanner call failed: no connection, a revoked or expired device (wipe), a wrong token, or anything else. */
export class TicketScannerApiError extends Error {
  constructor(readonly kind: 'offline' | 'revoked' | 'unauthorized' | 'other', readonly code?: string) {
    super(`Scanner call failed: ${kind}${code ? ' (' + code + ')' : ''}`);
  }
}

/** The device's calls (`X-Spark-Scanner` authenticated); see `SparkTicketScannerClient`. */
export interface TicketScannerApi {
  session(): Promise<SparkScannerSession>;
  listPage(next: string | null): Promise<SparkScannerListPage>;
  scan(request: SparkScanRequest): Promise<SparkScanResult>;
  sync(request: SparkScannerSyncRequest): Promise<SparkScannerSyncResult>;
}

/** Where the engine keeps its data: three partitions of the offline store, all wiped together. */
export interface TicketScannerStorage {
  /** The offline list, keyed by ticket code. */
  list: SparkOfflineStore<SparkScannerEntry>;
  /** Scans made offline, until a sync accepted them. */
  queue: SparkOfflineStore<SparkOfflineCommand<SparkOfflineScan>>;
  /** The session, the list cursor and the double entries reported. */
  meta: SparkOfflineStore<unknown>;
}

/** What the door staff see after a scan. */
export interface TicketScanOutcome {
  result: string;
  offline: boolean;
  code?: string | null;
  seat?: string | null;
  holder?: string | null;
  /** GB12: show the "ID check" banner. */
  minimumAge?: number | null;
  doubleEntry: boolean;
}

const SESSION = 'session';
const CURSOR = 'cursor';
const DOUBLES = 'doubleEntries';

export class TicketScannerEngine {
  private readonly queue: SparkOfflineQueue<SparkOfflineScan>;
  private session: SparkScannerSession | null = null;

  constructor(
    private readonly api: TicketScannerApi,
    private readonly storage: TicketScannerStorage,
    private readonly now: () => number = () => Date.now(),
    private readonly subtle?: SubtleCrypto,
  ) {
    this.queue = new SparkOfflineQueue<SparkOfflineScan>(storage.queue, now);
  }

  get current(): SparkScannerSession | null {
    return this.session;
  }

  /**
   * Loads the session: from the server when reachable (and stores it), else the stored one. Answers `null` when the
   * device was wiped (revoked, expired) or has never been online.
   */
  async start(): Promise<SparkScannerSession | null> {
    try {
      this.session = await this.api.session();
      await this.storage.meta.put(SESSION, this.session);
    } catch (error) {
      if (await this.wipeIfRevoked(error)) return null;
      this.session = ((await this.storage.meta.get(SESSION)) as SparkScannerSession | undefined) ?? null;
    }
    if (this.session && this.now() >= Date.parse(this.session.wipeAfter)) {
      await this.wipe();
      return null;
    }
    return this.session;
  }

  /** Downloads the whole offline list (S9), page by page; keeps the first page's cursor for the next sync. Answers the count. */
  async download(): Promise<number> {
    let next: string | null = null;
    let cursor: string | null = null;
    do {
      const page = await this.call(() => this.api.listPage(next));
      cursor ??= page.cursor;
      await this.storage.list.putMany(page.entries.map(e => [e.code, e] as const));
      next = page.next ?? null;
    } while (next);
    await this.storage.meta.put(CURSOR, cursor);
    this.cursor = cursor;
    return this.storage.list.count();
  }

  /** How many tickets the offline list holds and how many scans wait for a sync. */
  async status(): Promise<{ listed: number; queued: number; doubleEntries: string[] }> {
    return {
      listed: await this.storage.list.count(),
      queued: await this.queue.size(),
      doubleEntries: ((await this.storage.meta.get(DOUBLES)) as string[] | undefined) ?? [],
    };
  }

  /** Scans `payload`: online when the server answers, else against the offline list. */
  async scan(payload: string, direction: TicketScanDirection = 'In'): Promise<TicketScanOutcome> {
    const session = this.session;
    if (!session) throw new Error('Start the scanner first.');
    const scanId = crypto.randomUUID();
    const parsed = parseTicketPayload(payload);
    if (!parsed) return { result: TicketScanResult.unknown, offline: false, doubleEntry: false };
    try {
      const online = await this.api.scan({ payload, scanId, direction, dateId: session.dateId ?? null });
      await this.remember(parsed.code, online.result, online.dateId ?? null, direction);
      return { result: online.result, offline: false, code: online.code, seat: online.seat, holder: online.holder, minimumAge: online.minimumAge, doubleEntry: online.doubleEntry };
    } catch (error) {
      if (await this.wipeIfRevoked(error)) return { result: TicketScanResult.unknown, offline: false, doubleEntry: false };
      if (!(error instanceof TicketScannerApiError) || error.kind !== 'offline') throw error;
    }
    return this.scanOffline(parsed.code, parsed.secret, scanId, direction);
  }

  /** An offline scan: the secret against the list's verifier, the list's rules, then the queue. */
  async scanOffline(code: string, secret: string, scanId: string, direction: TicketScanDirection): Promise<TicketScanOutcome> {
    const session = this.session!;
    const entry = await this.storage.list.get(code);
    if (!entry || !(await verifyTicketSecret(entry, secret, this.subtle)))
      return { result: TicketScanResult.unknown, offline: true, code, doubleEntry: false };
    const at = this.now();
    const dateId = checkInDateFor(session.list, entry.dateIds, session.dates, session.dateId, at);
    const result = applyOfflineScan(entry, session.list, dateId, direction);
    if (result === TicketScanResult.admitted || result === TicketScanResult.exited || result === TicketScanResult.alreadyIn) {
      // Every scan that reached the presence logic goes to the server, the double entry too: the gate log keeps it.
      await this.queue.enqueue({ scanId, code, at: new Date(at).toISOString(), direction, dateId }, scanId);
      await this.storage.list.put(code, entry);
    }
    return {
      result, offline: true, code, seat: entry.seat, holder: entry.holder, minimumAge: entry.minimumAge,
      doubleEntry: result === TicketScanResult.alreadyIn,
    };
  }

  /**
   * Uploads queued scans and applies the list's changes since the cursor (S9). Double entries the server found, this
   * device's and other devices', are kept for the staff to see. Answers how many scans were sent.
   */
  async sync(): Promise<number> {
    let changesApplied = false;
    const sent = await this.queue.replay(async batch => {
      await this.applySync(await this.call(() => this.api.sync({ scans: batch.map(c => c.body), since: this.cursorSync() })));
      changesApplied = true;
    });
    if (!changesApplied) await this.applySync(await this.call(() => this.api.sync({ scans: [], since: this.cursorSync() })));
    return sent;
  }

  /** Deletes the list, the queue, the session and the reports (a revoked device, the end of the event). */
  async wipe(): Promise<void> {
    await this.storage.list.clear();
    await this.queue.clear();
    await this.storage.meta.clear();
    this.session = null;
    this.cursor = null;
  }

  // ---- internals ----------------------------------------------------------------------------------------------

  private cursor: string | null | undefined;

  private cursorSync(): string | null {
    return this.cursor ?? null;
  }

  private async applySync(result: SparkScannerSyncResult): Promise<void> {
    if (result.changes.length > 0) await this.storage.list.putMany(result.changes.map(e => [e.code, e] as const));
    const known = new Set(((await this.storage.meta.get(DOUBLES)) as string[] | undefined) ?? []);
    for (const id of result.doubleEntries) known.add(id);
    await this.storage.meta.put(DOUBLES, [...known]);
    this.cursor = result.cursor;
    await this.storage.meta.put(CURSOR, result.cursor);
  }

  /** Keeps the offline copy in step with an online scan, so a scan right after losing the connection decides alike. */
  private async remember(code: string, result: string, dateId: string | null, direction: TicketScanDirection): Promise<void> {
    if (!dateId || (result !== TicketScanResult.admitted && result !== TicketScanResult.exited)) return;
    const entry = await this.storage.list.get(code);
    if (!entry) return;
    if (direction === 'In') {
      if (!entry.inside.includes(dateId)) entry.inside = [...entry.inside, dateId];
      if (!entry.used.includes(dateId)) entry.used = [...entry.used, dateId];
    } else {
      entry.inside = entry.inside.filter(d => d !== dateId);
    }
    await this.storage.list.put(code, entry);
  }

  private async call<T>(work: () => Promise<T>): Promise<T> {
    if (this.cursor === undefined) this.cursor = ((await this.storage.meta.get(CURSOR)) as string | undefined) ?? null;
    try {
      return await work();
    } catch (error) {
      if (await this.wipeIfRevoked(error)) throw new TicketScannerApiError('revoked', 'deviceRevoked');
      throw error;
    }
  }

  private async wipeIfRevoked(error: unknown): Promise<boolean> {
    if (error instanceof TicketScannerApiError && (error.kind === 'revoked' || error.kind === 'unauthorized')) {
      await this.wipe();
      return true;
    }
    return false;
  }
}
