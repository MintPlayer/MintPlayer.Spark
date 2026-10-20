import { inject, Injectable } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Observable, filter, map } from 'rxjs';
import { SPARK_CONFIG } from '@mintplayer/ng-spark';
import { SparkLiveService, SparkService } from '@mintplayer/ng-spark/services';
import {
  WaitingRoomDrawProof,
  WaitingRoomEntryView,
  WaitingRoomFrame,
  WaitingRoomInfo,
  WaitingRoomJoinBody,
  WaitingRoomJoinResult,
  WaitingRoomPrivateFrame,
} from './generated/contracts';
import { firstValueFrom } from 'rxjs';

/** The topic of a room: public progress, and the same name for each identity's private stream. */
export const waitingRoomTopic = (key: string) => `waiting-room:${key}`;

/**
 * The client of `MintPlayer.Spark.WaitingRoom` (`/spark/waiting-room/*`, commerce #521 M16).
 *
 * Reads answer `null` on a refusal (404). Writes go through {@link SparkService.postEnvelope} (the app's XSRF header
 * goes with them); a join answers its outcome whatever the status (428 challenge, 403, 409, 429), never throws for it.
 */
@Injectable({ providedIn: 'root' })
export class SparkWaitingRoomService {
  private readonly http = inject(HttpClient);
  private readonly spark = inject(SparkService);
  private readonly live = inject(SparkLiveService);
  private readonly baseUrl = inject(SPARK_CONFIG, { optional: true })?.baseUrl ?? '/spark';

  info(key: string): Promise<WaitingRoomInfo | null> {
    return this.get<WaitingRoomInfo>(`/waiting-room/${encodeURIComponent(key)}`);
  }

  /** The caller's entry, or null when it has none. */
  entry(key: string): Promise<WaitingRoomEntryView | null> {
    return this.get<WaitingRoomEntryView>(`/waiting-room/${encodeURIComponent(key)}/entry`);
  }

  /** The proof of the shuffle or draw. */
  draw(key: string): Promise<WaitingRoomDrawProof | null> {
    return this.get<WaitingRoomDrawProof>(`/waiting-room/${encodeURIComponent(key)}/draw`);
  }

  /** Joins; idempotent per identity. */
  async join(key: string, body: WaitingRoomJoinBody = {}): Promise<WaitingRoomJoinResult> {
    try {
      return await this.spark.postEnvelope<WaitingRoomJoinResult>(`/waiting-room/${encodeURIComponent(key)}/join`, { ...body });
    } catch (e) {
      const result = (e as HttpErrorResponse)?.error?.result as WaitingRoomJoinResult | undefined;
      if (result?.outcome) return result;
      throw e;
    }
  }

  /** A sign of life: answers a presence check, brings a missed entry back at its rank. */
  async presence(key: string, visible: boolean): Promise<WaitingRoomEntryView | null> {
    try {
      return await this.spark.postEnvelope<WaitingRoomEntryView>(`/waiting-room/${encodeURIComponent(key)}/presence`, { visible });
    } catch (e) {
      if ((e as HttpErrorResponse)?.status === 404) return null;
      throw e;
    }
  }

  leave(key: string): Promise<unknown> {
    return this.spark.postEnvelope(`/waiting-room/${encodeURIComponent(key)}/leave`, {});
  }

  /** The room's public progress (latest value at once, then every change). */
  frames(key: string): Observable<WaitingRoomFrame> {
    return this.live.watchTopic<WaitingRoomFrame>(waitingRoomTopic(key)).pipe(
      filter(event => event.type === 'topic'),
      map(event => (event as { data: WaitingRoomFrame }).data));
  }

  /** This browser's own stream: called, admitted, missed, lost. */
  privateFrames(key: string): Observable<WaitingRoomPrivateFrame> {
    return this.live.watchPrivate<WaitingRoomPrivateFrame>(waitingRoomTopic(key)).pipe(
      filter(event => event.type === 'topic'),
      map(event => (event as { data: WaitingRoomPrivateFrame }).data));
  }

  private async get<T>(path: string): Promise<T | null> {
    try {
      const envelope = await firstValueFrom(this.http.get<{ result?: T } | null>(`${this.baseUrl}${path}`));
      return envelope?.result ?? null;
    } catch (e) {
      const status = (e as HttpErrorResponse)?.status;
      if (status === 404 || status === 401) return null;
      throw e;
    }
  }
}
