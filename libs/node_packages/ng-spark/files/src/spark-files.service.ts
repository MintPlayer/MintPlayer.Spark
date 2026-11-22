import { inject, Injectable } from '@angular/core';
import { HttpBackend, HttpClient, HttpErrorResponse, HttpEvent, HttpEventType, HttpHeaders } from '@angular/common/http';
import { firstValueFrom, Observable } from 'rxjs';
import { filter, map } from 'rxjs/operators';
import { SPARK_CONFIG } from '@mintplayer/ng-spark';
import { SparkFileScanStatus } from '@mintplayer/ng-spark/models';

/** A slot from `POST /spark/files/prepare`: where and how to send one file's bytes, and the token to commit. */
export interface SparkPreparedFile {
  token: string;
  fileName: string;
  url: string;
  /** `POST`: a multipart form of `fields`, in order, then the file as `file`. `PUT`: the raw bytes with `headers`. */
  method: 'POST' | 'PUT' | string;
  fields: Record<string, string>;
  headers: Record<string, string>;
  /** The bytes go straight to the store (a presigned URL), not through Spark. */
  direct: boolean;
}

/** A file as `POST /spark/files/commit` checked it. Claimable when `error` is null and the scan status is `Clean`. */
export interface SparkCommittedFile {
  token: string;
  fileName: string | null;
  contentType: string | null;
  length: number;
  scanStatus: SparkFileScanStatus | number;
  width: number | null;
  height: number | null;
  error: string | null;
}

/** How long {@link SparkFilesService.commitUntilScanned} waits for a deferred scan before it gives up. */
export interface SparkCommitPolling {
  intervalMs?: number;
  attempts?: number;
  signal?: AbortSignal;
}

/**
 * The Files endpoints (#700): prepare a slot, send the bytes, commit them. A save then claims the token.
 *
 * A direct upload goes through a bare `HttpClient` on the `HttpBackend`, past every interceptor: the bytes go to the
 * store's presigned URL, which must get neither the antiforgery header nor an app's auth header. A Spark upload uses the
 * app's `HttpClient`, so the antiforgery header is added as for any other Spark POST.
 */
@Injectable({ providedIn: 'root' })
export class SparkFilesService {
  private readonly http = inject(HttpClient);
  private readonly bare = new HttpClient(inject(HttpBackend));
  private readonly baseUrl = inject(SPARK_CONFIG, { optional: true })?.baseUrl ?? '/spark';

  async prepare(objectTypeId: string, attribute: string, id: string | undefined, files: readonly File[]): Promise<SparkPreparedFile[]> {
    const body = {
      objectTypeId,
      attribute,
      id: id ?? null,
      files: files.map(f => ({ fileName: f.name, length: f.size })),
    };
    const result = await firstValueFrom(this.http.post<{ files: SparkPreparedFile[] }>(`${this.baseUrl}/files/prepare`, body));
    return result.files;
  }

  /** Sends `file` to its slot; `onProgress` gets the bytes sent so far. */
  upload(prepared: SparkPreparedFile, file: File, onProgress?: (sent: number) => void): Promise<void> {
    const client = prepared.direct ? this.bare : this.http;
    let request: Observable<HttpEvent<unknown>>;
    if (prepared.method.toUpperCase() === 'PUT') {
      request = client.put(prepared.url, file, {
        headers: new HttpHeaders(prepared.headers ?? {}),
        reportProgress: true,
        observe: 'events',
      });
    } else {
      const form = new FormData();
      for (const [name, value] of Object.entries(prepared.fields ?? {})) form.append(name, value);
      form.append('file', file, file.name);
      request = client.post(prepared.url, form, {
        headers: new HttpHeaders(prepared.headers ?? {}),
        reportProgress: true,
        observe: 'events',
      });
    }
    return firstValueFrom(request.pipe(
      filter(event => {
        if (event.type === HttpEventType.UploadProgress) onProgress?.(event.loaded);
        return event.type === HttpEventType.Response;
      }),
      map(() => undefined),
    ));
  }

  async commit(tokens: readonly string[]): Promise<SparkCommittedFile[]> {
    const result = await firstValueFrom(this.http.post<{ files: SparkCommittedFile[] }>(`${this.baseUrl}/files/commit`, { tokens }));
    return result.files;
  }

  /**
   * Commits, and while a deferred scan still answers `Pending`, commits again after `intervalMs`, at most `attempts`
   * times (default 2 s × 30). Returns the last answer: a file still `Pending` then is one the scanner has not reached.
   */
  async commitUntilScanned(tokens: readonly string[], polling: SparkCommitPolling = {}): Promise<SparkCommittedFile[]> {
    const intervalMs = polling.intervalMs ?? 2000;
    const attempts = polling.attempts ?? 30;
    let files = await this.commit(tokens);
    for (let attempt = 1; attempt < attempts && files.some(isPending) && !polling.signal?.aborted; attempt++) {
      await delay(intervalMs, polling.signal);
      if (polling.signal?.aborted) break;
      files = await this.commit(tokens);
    }
    return files;
  }

  /** The server's `{ error }` text of a failed call, else null. */
  static errorText(error: unknown): string | null {
    if (error instanceof HttpErrorResponse) {
      const body = error.error;
      if (body && typeof body === 'object' && typeof body.error === 'string') return body.error;
    }
    return null;
  }
}

function isPending(file: SparkCommittedFile): boolean {
  return file.error === null && (file.scanStatus === 'Pending' || file.scanStatus === 0);
}

function delay(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise(resolve => {
    const timer = setTimeout(resolve, ms);
    signal?.addEventListener('abort', () => {
      clearTimeout(timer);
      resolve();
    }, { once: true });
  });
}
