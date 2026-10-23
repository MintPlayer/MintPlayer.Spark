import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { SPARK_CONFIG } from '@mintplayer/ng-spark';
import { SparkBoard, SparkBoardCatalog, SparkBoardSaveRequest } from './spark-board.models';

/**
 * `/spark/boards` (`MintPlayer.Spark.Boards`, #530). The writes carry the antiforgery header through
 * the app's `HttpClient` like every other Spark write; a stale change vector answers 409.
 */
@Injectable({ providedIn: 'root' })
export class SparkBoardsService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = `${inject(SPARK_CONFIG, { optional: true })?.baseUrl ?? '/spark'}/boards`;

  get(boardId: string): Promise<SparkBoard> {
    return firstValueFrom(this.http.get<SparkBoard>(this.url(boardId)));
  }

  save(boardId: string, request: SparkBoardSaveRequest): Promise<SparkBoard> {
    return firstValueFrom(this.http.put<SparkBoard>(this.url(boardId), request));
  }

  /** Deletes the caller's layout; answers the application's default. */
  reset(boardId: string): Promise<SparkBoard> {
    return firstValueFrom(this.http.delete<SparkBoard>(this.url(boardId)));
  }

  catalog(boardId: string): Promise<SparkBoardCatalog> {
    return firstValueFrom(this.http.get<SparkBoardCatalog>(`${this.url(boardId)}/catalog`));
  }

  private url(boardId: string): string {
    return `${this.baseUrl}/${encodeURIComponent(boardId)}`;
  }
}
