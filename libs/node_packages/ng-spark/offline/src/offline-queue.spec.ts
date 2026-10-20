import { describe, expect, it } from 'vitest';
import { SparkOfflineCommand, SparkOfflineQueue } from './offline-queue';
import { openSparkOfflineStore, SparkMemoryStore } from './offline-store';

// Warehouse WX7 / commerce #521 M15 (S9): the offline store and command queue shared by the door scanner and the
// warehouse handhelds.

describe('SparkMemoryStore', () => {
  it('stores copies, counts and clears a partition', async () => {
    const store = new SparkMemoryStore<{ n: number }>();
    const value = { n: 1 };
    await store.put('a', value);
    value.n = 2; // a stored value is a copy
    await store.putMany([['b', { n: 3 }], ['c', { n: 4 }]]);
    expect(await store.get('a')).toEqual({ n: 1 });
    expect(await store.count()).toBe(3);
    expect((await store.keys()).sort()).toEqual(['a', 'b', 'c']);
    await store.delete('b');
    expect(await store.get('b')).toBeUndefined();
    await store.clear();
    expect(await store.count()).toBe(0);
  });

  it('is what openSparkOfflineStore answers without IndexedDB', async () => {
    const hadIndexedDb = 'indexedDB' in globalThis;
    const saved = (globalThis as { indexedDB?: unknown }).indexedDB;
    delete (globalThis as { indexedDB?: unknown }).indexedDB;
    try {
      expect(await openSparkOfflineStore('test')).toBeInstanceOf(SparkMemoryStore);
    } finally {
      if (hadIndexedDb) (globalThis as { indexedDB?: unknown }).indexedDB = saved;
    }
  });
});

describe('SparkOfflineQueue', () => {
  it('replays oldest first in batches and removes only what was sent', async () => {
    let clock = 1_000;
    const queue = new SparkOfflineQueue<string>(new SparkMemoryStore<SparkOfflineCommand<string>>(), () => clock++);
    await queue.enqueue('first', 'id-1');
    await queue.enqueue('second', 'id-2');
    await queue.enqueue('third', 'id-3');

    const batches: string[][] = [];
    let fail = true;
    const send = async (batch: SparkOfflineCommand<string>[]) => {
      if (batches.length === 1 && fail) throw new Error('offline');
      batches.push(batch.map(c => c.body));
    };
    await expect(queue.replay(send, 2)).rejects.toThrow('offline');
    expect(batches).toEqual([['first', 'second']]);
    expect(await queue.size()).toBe(1);

    fail = false;
    expect(await queue.replay(send, 2)).toBe(1);
    expect(batches).toEqual([['first', 'second'], ['third']]);
    expect(await queue.size()).toBe(0);
  });

  it('runs one replay at a time', async () => {
    const queue = new SparkOfflineQueue<number>(new SparkMemoryStore<SparkOfflineCommand<number>>());
    await queue.enqueue(1, 'a');
    let calls = 0;
    const send = async () => {
      calls++;
      await new Promise(r => setTimeout(r, 5));
    };
    const [x, y] = await Promise.all([queue.replay(send), queue.replay(send)]);
    expect(calls).toBe(1);
    expect(x).toBe(1);
    expect(y).toBe(1);
  });

  it('keeps the command id: a re-sent command is the same command', async () => {
    const queue = new SparkOfflineQueue<string>(new SparkMemoryStore<SparkOfflineCommand<string>>(), () => 42);
    const command = await queue.enqueue('x', 'fixed-id');
    expect(command).toEqual({ id: 'fixed-id', at: 42, body: 'x' });
    await queue.enqueue('x', 'fixed-id');
    expect(await queue.size()).toBe(1);
  });
});
