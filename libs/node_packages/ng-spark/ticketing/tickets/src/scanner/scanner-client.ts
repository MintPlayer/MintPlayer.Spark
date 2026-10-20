import { inject, Injectable } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpHeaders, HttpParams } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { SPARK_CONFIG } from '@mintplayer/ng-spark';
import {
  SparkScannerListPage,
  SparkScannerSession,
  SparkScannerSyncRequest,
  SparkScannerSyncResult,
  SparkScanRequest,
  SparkScanResult,
} from '../generated/contracts';
import { TicketScannerApi, TicketScannerApiError } from './ticket-scanner';

/** The header a door device authenticates with: `{deviceKey}.{token}` (shown once at registration). */
export const SPARK_SCANNER_HEADER = 'X-Spark-Scanner';

/** Maps a failed call: no connection → `offline`; 409 `deviceRevoked` → `revoked`; 401 → `unauthorized`. */
export function scannerErrorOf(error: unknown): TicketScannerApiError {
  if (error instanceof TicketScannerApiError) return error;
  if (error instanceof HttpErrorResponse) {
    if (error.status === 0 || error.status === 502 || error.status === 503 || error.status === 504) return new TicketScannerApiError('offline');
    if (error.status === 401) return new TicketScannerApiError('unauthorized');
    const body = error.error as { result?: { code?: string } ; code?: string } | null | undefined;
    const code = body?.result?.code ?? body?.code;
    if (error.status === 409 && code === 'deviceRevoked') return new TicketScannerApiError('revoked', code);
    return new TicketScannerApiError('other', code);
  }
  return new TicketScannerApiError('offline');
}

/**
 * The door device's client of `/spark/ticketing/tickets/scanner/*`. It sends only the device credential, never the
 * staff member's cookie session (no antiforgery token either: the header is not ambient). `withCredential` binds it
 * to one device.
 */
@Injectable({ providedIn: 'root' })
export class SparkTicketScannerClient {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(SPARK_CONFIG, { optional: true })?.baseUrl ?? '/spark';

  /** A {@link TicketScannerApi} for the device credential `{deviceKey}.{token}`. */
  withCredential(credential: string): TicketScannerApi {
    const headers = new HttpHeaders({ [SPARK_SCANNER_HEADER]: credential });
    const url = (path: string) => `${this.baseUrl}/ticketing/tickets/scanner${path}`;
    const run = async <T>(call: () => Promise<{ result?: T } | null>): Promise<T> => {
      try {
        const envelope = await call();
        if (!envelope?.result) throw new TicketScannerApiError('other');
        return envelope.result;
      } catch (error) {
        throw scannerErrorOf(error);
      }
    };
    return {
      session: () => run(() => firstValueFrom(this.http.get<{ result?: SparkScannerSession }>(url('/session'), { headers, withCredentials: false }))),
      listPage: next => run(() => firstValueFrom(this.http.get<{ result?: SparkScannerListPage }>(url('/list'), {
        headers, withCredentials: false, params: next ? new HttpParams().set('next', next) : undefined,
      }))),
      scan: (request: SparkScanRequest) => run(() => firstValueFrom(this.http.post<{ result?: SparkScanResult }>(url('/scan'), request, { headers, withCredentials: false }))),
      sync: (request: SparkScannerSyncRequest) => run(() => firstValueFrom(this.http.post<{ result?: SparkScannerSyncResult }>(url('/sync'), request, { headers, withCredentials: false }))),
    };
  }
}
