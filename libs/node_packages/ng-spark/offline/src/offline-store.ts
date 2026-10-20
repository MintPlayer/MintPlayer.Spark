// Offline storage for Spark clients (warehouse PRD WX7, commerce PRD §4.11 "Scanner PWA"): one key-value store per
// partition, backed by IndexedDB in the browser and by memory in tests or where IndexedDB is missing. A partition is
// wiped as a whole (sign-out, a revoked device, the end of an event), so nothing a user or a device downloaded outlives
// its purpose. The shared base of the door scanner and the warehouse handhelds.

/** A key-value store of one partition. Values are structured-cloneable. */
export interface SparkOfflineStore<T = unknown> {
  get(key: string): Promise<T | undefined>;
  put(key: string, value: T): Promise<void>;
  /** Writes many values in one transaction (an offline list page). */
  putMany(entries: ReadonlyArray<readonly [string, T]>): Promise<void>;
  delete(key: string): Promise<void>;
  keys(): Promise<string[]>;
  values(): Promise<T[]>;
  count(): Promise<number>;
  /** Deletes every value of the partition. */
  clear(): Promise<void>;
}

/** A store in memory: tests, server rendering, browsers without IndexedDB. */
export class SparkMemoryStore<T = unknown> implements SparkOfflineStore<T> {
  private readonly map = new Map<string, T>();

  async get(key: string): Promise<T | undefined> {
    const value = this.map.get(key);
    return value === undefined ? undefined : structuredClone(value);
  }

  async put(key: string, value: T): Promise<void> {
    this.map.set(key, structuredClone(value));
  }

  async putMany(entries: ReadonlyArray<readonly [string, T]>): Promise<void> {
    for (const [key, value] of entries) this.map.set(key, structuredClone(value));
  }

  async delete(key: string): Promise<void> {
    this.map.delete(key);
  }

  async keys(): Promise<string[]> {
    return [...this.map.keys()];
  }

  async values(): Promise<T[]> {
    return [...this.map.values()].map(v => structuredClone(v));
  }

  async count(): Promise<number> {
    return this.map.size;
  }

  async clear(): Promise<void> {
    this.map.clear();
  }
}

const DATABASE = 'spark-offline';

function request<R>(req: IDBRequest<R>): Promise<R> {
  return new Promise((resolve, reject) => {
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

function done(tx: IDBTransaction): Promise<void> {
  return new Promise((resolve, reject) => {
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(tx.error);
    tx.onabort = () => reject(tx.error);
  });
}

/**
 * A store in IndexedDB: database `spark-offline`, one object store per partition (`{area}:{owner}`, e.g.
 * `scanner-list:{deviceId}`). Opening a new partition bumps the database version to add its object store.
 */
export class SparkIndexedDbStore<T = unknown> implements SparkOfflineStore<T> {
  private constructor(private readonly db: IDBDatabase, private readonly name: string) {}

  static async open<T>(partition: string, factory: IDBFactory = indexedDB): Promise<SparkIndexedDbStore<T>> {
    let db = await request(factory.open(DATABASE));
    if (!db.objectStoreNames.contains(partition)) {
      const version = db.version + 1;
      db.close();
      const upgrade = factory.open(DATABASE, version);
      upgrade.onupgradeneeded = () => {
        const target = upgrade.result;
        if (!target.objectStoreNames.contains(partition)) target.createObjectStore(partition);
      };
      db = await request(upgrade);
    }
    // Another tab that opens a newer version asks us to let go.
    db.onversionchange = () => db.close();
    return new SparkIndexedDbStore<T>(db, partition);
  }

  private store(mode: IDBTransactionMode): { tx: IDBTransaction; os: IDBObjectStore } {
    const tx = this.db.transaction(this.name, mode);
    return { tx, os: tx.objectStore(this.name) };
  }

  async get(key: string): Promise<T | undefined> {
    return (await request(this.store('readonly').os.get(key))) as T | undefined;
  }

  async put(key: string, value: T): Promise<void> {
    const { tx, os } = this.store('readwrite');
    os.put(value, key);
    await done(tx);
  }

  async putMany(entries: ReadonlyArray<readonly [string, T]>): Promise<void> {
    const { tx, os } = this.store('readwrite');
    for (const [key, value] of entries) os.put(value, key);
    await done(tx);
  }

  async delete(key: string): Promise<void> {
    const { tx, os } = this.store('readwrite');
    os.delete(key);
    await done(tx);
  }

  async keys(): Promise<string[]> {
    return (await request(this.store('readonly').os.getAllKeys())).map(String);
  }

  async values(): Promise<T[]> {
    return (await request(this.store('readonly').os.getAll())) as T[];
  }

  async count(): Promise<number> {
    return request(this.store('readonly').os.count());
  }

  async clear(): Promise<void> {
    const { tx, os } = this.store('readwrite');
    os.clear();
    await done(tx);
  }
}

/** Opens `partition` in IndexedDB when the browser has it, else in memory. */
export async function openSparkOfflineStore<T>(partition: string): Promise<SparkOfflineStore<T>> {
  if (typeof indexedDB === 'undefined') return new SparkMemoryStore<T>();
  try {
    return await SparkIndexedDbStore.open<T>(partition);
  } catch {
    return new SparkMemoryStore<T>();
  }
}

/**
 * Asks the browser to keep this origin's storage under pressure (`navigator.storage.persist()`). Without it an offline
 * list may be evicted; the caller shows a warning when this answers `false`.
 */
export async function requestPersistentStorage(): Promise<boolean> {
  const storage = typeof navigator === 'undefined' ? undefined : navigator.storage;
  if (!storage?.persist) return false;
  try {
    return (await storage.persisted?.()) || (await storage.persist());
  } catch {
    return false;
  }
}
