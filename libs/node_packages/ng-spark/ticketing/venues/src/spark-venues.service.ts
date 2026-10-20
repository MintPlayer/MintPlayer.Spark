import { inject, Injectable } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { SPARK_CONFIG } from '@mintplayer/ng-spark';
import { SparkService } from '@mintplayer/ng-spark/services';
import {
  LayoutDiffResult,
  LayoutPlan,
  SeatMapPayload,
  SeatStateFrame,
  VenueLayoutPlanResponse,
  VenueLayoutProblem,
  VenueLayoutPublishResponse,
  VenueLayoutRef,
  VenueLayoutSaveResponse,
} from './generated/contracts';

/** A save that was refused: the draft moved on (`conflict`, reload) or the plan has problems. */
export class SparkVenueSaveError extends Error {
  constructor(readonly kind: 'conflict' | 'invalid', readonly revision: number, readonly problems: VenueLayoutProblem[]) {
    super(kind === 'conflict' ? 'The layout changed since it was loaded.' : 'The layout has problems.');
  }
}

/**
 * Client for `MintPlayer.Spark.Commerce.Ticketing.Venues` (`/spark/ticketing/venues/*`, commerce #521 M14).
 *
 * POSTs go through {@link SparkService.postEnvelope} (the app's XSRF header goes with them). Ids travel in bodies and
 * query strings, never in paths (RavenDB ids contain slashes).
 */
@Injectable({ providedIn: 'root' })
export class SparkVenuesService {
  private readonly spark = inject(SparkService);
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(SPARK_CONFIG, { optional: true })?.baseUrl ?? '/spark';

  private url(path: string): string {
    return `${this.baseUrl}/ticketing/venues${path}`;
  }

  /** `GET /seatmap`: a snapshot's packed seat map (anonymous); null when there is none. */
  async seatMap(snapshotId: string): Promise<SeatMapPayload | null> {
    return this.getOrNull<SeatMapPayload>('/seatmap', { snapshotId });
  }

  /** `GET /seatmap/state`: the seat states now; null when there is no such snapshot. */
  async seatStates(snapshotId: string): Promise<SeatStateFrame | null> {
    return this.getOrNull<SeatStateFrame>('/seatmap/state', { snapshotId });
  }

  /** The URL of a snapshot's background image (anonymous). */
  seatMapBackgroundUrl(snapshotId: string): string {
    return `${this.url('/seatmap/background')}?snapshotId=${encodeURIComponent(snapshotId)}`;
  }

  /** The URL of an uploaded background image (signed-in). */
  backgroundUrl(imageId: string): string {
    return `${this.url('/background')}?imageId=${encodeURIComponent(imageId)}`;
  }

  /** The URL of a layout's seats as CSV. */
  csvUrl(layoutId: string, version = 'draft'): string {
    return `${this.url('/layouts/csv')}?layoutId=${encodeURIComponent(layoutId)}&version=${encodeURIComponent(version)}`;
  }

  /** `GET /layouts/plan`: `draft` and `proposal` for the owner and approvers, `published` or a number for everyone signed in. */
  async plan(layoutId: string, version = 'draft'): Promise<VenueLayoutPlanResponse | null> {
    return this.getOrNull<VenueLayoutPlanResponse>('/layouts/plan', { layoutId, version });
  }

  /** `GET /layouts/diff`: the pending publication against the published version. */
  async diff(layoutId: string): Promise<LayoutDiffResult | null> {
    return this.getOrNull<LayoutDiffResult>('/layouts/diff', { layoutId });
  }

  async createLayout(venueId: string, name: string): Promise<VenueLayoutRef> {
    return this.spark.postEnvelope<VenueLayoutRef>('/ticketing/venues/layouts/create', { venueId, name });
  }

  async copyLayout(layoutId: string, name: string): Promise<VenueLayoutRef> {
    return this.spark.postEnvelope<VenueLayoutRef>('/ticketing/venues/layouts/copy', { layoutId, name });
  }

  /** Saves the draft; throws {@link SparkVenueSaveError} on a stale revision (409) or problems (400). */
  async saveDraft(layoutId: string, revision: number, plan: LayoutPlan): Promise<number> {
    return this.saving(() => this.spark.postEnvelope<VenueLayoutSaveResponse>('/ticketing/venues/layouts/draft', { layoutId, revision, plan }));
  }

  /** Merges a CSV into the draft on the server; throws {@link SparkVenueSaveError}. */
  async importCsv(layoutId: string, revision: number, csv: string): Promise<number> {
    return this.saving(() => this.spark.postEnvelope<VenueLayoutSaveResponse>('/ticketing/venues/layouts/csv/import', { layoutId, revision, csv }));
  }

  /** Publishes the draft, or asks an approver (D8). Throws {@link SparkVenueSaveError} like a save. */
  async publish(layoutId: string, revision: number): Promise<VenueLayoutPublishResponse> {
    try {
      return await this.spark.postEnvelope<VenueLayoutPublishResponse>('/ticketing/venues/layouts/publish', { layoutId, revision });
    } catch (e) {
      throw this.saveError(e) ?? e;
    }
  }

  /** Uploads a background image (PNG, JPEG or WebP); answers its id. */
  async uploadBackground(layoutId: string, file: Blob): Promise<string> {
    const envelope = await firstValueFrom(this.http.post<{ result?: VenueLayoutRef } | null>(
      `${this.url('/layouts/background')}?layoutId=${encodeURIComponent(layoutId)}`, file,
      { headers: { 'Content-Type': file.type || 'application/octet-stream' } }));
    return envelope?.result?.layoutId ?? '';
  }

  private async saving(post: () => Promise<VenueLayoutSaveResponse>): Promise<number> {
    try {
      return (await post()).revision;
    } catch (e) {
      throw this.saveError(e) ?? e;
    }
  }

  private saveError(e: unknown): SparkVenueSaveError | null {
    const error = e as HttpErrorResponse;
    const body = (error?.error?.result ?? error?.error) as VenueLayoutSaveResponse | undefined;
    if (error?.status === 409) return new SparkVenueSaveError('conflict', body?.revision ?? 0, []);
    if (error?.status === 400 && Array.isArray(body?.problems)) return new SparkVenueSaveError('invalid', 0, body!.problems);
    return null;
  }

  private async getOrNull<T>(path: string, params: Record<string, string>): Promise<T | null> {
    try {
      const envelope = await firstValueFrom(this.http.get<{ result?: T } | null>(this.url(path), { params }));
      return envelope?.result ?? null;
    } catch (e) {
      const status = (e as HttpErrorResponse)?.status;
      if (status === 404 || status === 401) return null;
      throw e;
    }
  }
}
