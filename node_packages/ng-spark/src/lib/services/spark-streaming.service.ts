import { inject, Injectable, NgZone } from '@angular/core';
import { Observable } from 'rxjs';
import { StreamingMessage } from '../models/streaming-message';
import { SPARK_CONFIG } from '../models/spark-config';

@Injectable({ providedIn: 'root' })
export class SparkStreamingService {
  private readonly config = inject(SPARK_CONFIG, { optional: true });
  private readonly baseUrl = this.config?.baseUrl ?? '/spark';
  private readonly ngZone = inject(NgZone);

  connectToStreamingQuery(queryId: string): Observable<StreamingMessage> {
    return new Observable<StreamingMessage>(subscriber => {
      let ws: WebSocket | null = null;
      let retryCount = 0;
      let retryTimeout: ReturnType<typeof setTimeout> | null = null;
      let closed = false;
      const maxRetries = 10;
      const maxDelay = 30000;

      const connect = () => {
        if (closed) return;

        const url = this.buildWebSocketUrl(queryId);
        ws = new WebSocket(url);

        ws.onopen = () => {
          retryCount = 0; // Reset on successful connect
        };

        ws.onmessage = (event) => {
          try {
            const message: StreamingMessage = JSON.parse(event.data);
            this.ngZone.run(() => subscriber.next(message));
          } catch {
            // Ignore malformed messages
          }
        };

        ws.onerror = () => {
          // Error will trigger onclose
        };

        ws.onclose = (event) => {
          if (closed) return;

          // Don't reconnect on normal closure
          if (event.code === 1000) {
            this.ngZone.run(() => subscriber.complete());
            return;
          }

          // Reconnect with exponential backoff
          if (retryCount < maxRetries) {
            const delay = Math.min(1000 * Math.pow(2, retryCount), maxDelay);
            retryCount++;
            retryTimeout = setTimeout(() => connect(), delay);
          } else {
            this.ngZone.run(() => subscriber.error(new Error('WebSocket connection failed after maximum retries')));
          }
        };
      };

      connect();

      // Teardown: close WebSocket on unsubscribe
      return () => {
        closed = true;
        if (retryTimeout) {
          clearTimeout(retryTimeout);
        }
        if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) {
          ws.close(1000, 'Client unsubscribed');
        }
      };
    });
  }

  private buildWebSocketUrl(queryId: string): string {
    const encodedId = encodeURIComponent(queryId);
    const path = `${this.baseUrl}/queries/${encodedId}/stream`;

    // If baseUrl is absolute (starts with http/https), replace protocol
    if (this.baseUrl.startsWith('http://')) {
      return path.replace(/^http:\/\//, 'ws://');
    }
    if (this.baseUrl.startsWith('https://')) {
      return path.replace(/^https:\/\//, 'wss://');
    }

    // Relative URL — construct from window.location
    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    return `${protocol}//${window.location.host}${path}`;
  }
}
