import { Injectable, NgZone, inject, signal } from '@angular/core';

export interface FileChangedMessage {
  type: 'fileChanged';
  filePath: string;
  fileName: string;
  changeType: 'Changed' | 'Created' | 'Deleted' | 'Renamed';
  affectedEntities: string[];
}

@Injectable({ providedIn: 'root' })
export class SparkFileWatchService {
  private ngZone = inject(NgZone);
  private ws: WebSocket | null = null;
  private retryCount = 0;
  private retryTimeout: ReturnType<typeof setTimeout> | null = null;

  readonly fileChanged = signal<FileChangedMessage | null>(null);

  connect(): void {
    const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const wsUrl = `${protocol}//${location.host}/spark/editor/file-events`;

    this.ws = new WebSocket(wsUrl);

    this.ws.onopen = () => {
      this.retryCount = 0;
    };

    this.ws.onmessage = (event: MessageEvent) => {
      const message: FileChangedMessage = JSON.parse(event.data);
      this.ngZone.run(() => this.fileChanged.set(message));
    };

    this.ws.onclose = (event: CloseEvent) => {
      if (event.code !== 1000 && this.retryCount < 10) {
        const delay = Math.min(1000 * Math.pow(2, this.retryCount), 30000);
        this.retryCount++;
        this.retryTimeout = setTimeout(() => this.connect(), delay);
      }
    };

    this.ws.onerror = () => {
      // onclose will fire after onerror, reconnection handled there
    };
  }

  disconnect(): void {
    if (this.retryTimeout) {
      clearTimeout(this.retryTimeout);
      this.retryTimeout = null;
    }
    if (this.ws) {
      this.ws.close(1000, 'Client disconnected');
      this.ws = null;
    }
  }
}
