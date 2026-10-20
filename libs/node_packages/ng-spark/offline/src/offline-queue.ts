import { SparkOfflineStore } from './offline-store';

/** A command made offline: its own id (the server's idempotency key), the device's time, and its body. */
export interface SparkOfflineCommand<T> {
  /** A UUID: re-sending the command changes nothing on the server. */
  id: string;
  /** The device's clock when the command was made (Unix milliseconds). */
  at: number;
  body: T;
}

/**
 * A durable queue of commands made offline (warehouse PRD WX7, commerce S9): kept in a {@link SparkOfflineStore}
 * partition until a replay succeeds. Replays are exactly-once from the queue's side (a command leaves only after the
 * server accepted the batch it was in) and idempotent on the server's (the command id), so a crash between "sent"
 * and "removed" re-sends harmlessly. One replay runs at a time.
 */
export class SparkOfflineQueue<T> {
  private replaying: Promise<number> | null = null;

  constructor(private readonly store: SparkOfflineStore<SparkOfflineCommand<T>>, private readonly now: () => number = () => Date.now()) {}

  /** Queues `body`; answers the command. */
  async enqueue(body: T, id: string = crypto.randomUUID()): Promise<SparkOfflineCommand<T>> {
    const command: SparkOfflineCommand<T> = { id, at: this.now(), body };
    await this.store.put(id, command);
    return command;
  }

  /** The queued commands, oldest first. */
  async pending(): Promise<SparkOfflineCommand<T>[]> {
    return (await this.store.values()).sort((a, b) => a.at - b.at || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0));
  }

  async size(): Promise<number> {
    return this.store.count();
  }

  /**
   * Sends the queued commands in batches of `batchSize` through `send`, removing each batch once `send` resolves.
   * A rejected `send` keeps the batch (and the rest) for the next replay. Answers how many were sent.
   */
  replay(send: (batch: SparkOfflineCommand<T>[]) => Promise<void>, batchSize = 500): Promise<number> {
    this.replaying ??= (async () => {
      try {
        let sent = 0;
        const all = await this.pending();
        for (let i = 0; i < all.length; i += batchSize) {
          const batch = all.slice(i, i + batchSize);
          await send(batch);
          for (const command of batch) await this.store.delete(command.id);
          sent += batch.length;
        }
        return sent;
      } finally {
        this.replaying = null;
      }
    })();
    return this.replaying;
  }

  /** Drops every queued command (a wipe). */
  clear(): Promise<void> {
    return this.store.clear();
  }
}
