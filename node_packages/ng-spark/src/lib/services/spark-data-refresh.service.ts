import { Injectable, signal } from '@angular/core';

/**
 * Service that allows external systems (e.g. file watchers, WebSocket notifications)
 * to signal Spark components that their data has changed and should be refreshed.
 *
 * Components subscribe to the `refreshTrigger` signal and re-fetch their data
 * when it changes and matches their entity type.
 */
@Injectable({ providedIn: 'root' })
export class SparkDataRefreshService {
  /**
   * Incremented each time a refresh is requested.
   * Components use effect() on this signal to detect changes.
   */
  readonly refreshTrigger = signal<DataRefreshEvent | null>(null);

  /**
   * Signal that data for the given entity types should be refreshed.
   * Components displaying any of these types will re-fetch from the server.
   */
  refresh(affectedEntities: string[]): void {
    this.refreshTrigger.set({
      affectedEntities,
      timestamp: Date.now()
    });
  }
}

export interface DataRefreshEvent {
  affectedEntities: string[];
  timestamp: number;
}
