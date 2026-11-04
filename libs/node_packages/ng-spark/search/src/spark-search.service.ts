import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom, Observable } from 'rxjs';
import { SPARK_CONFIG } from '@mintplayer/ng-spark';
import { SparkSearchHit } from './spark-search.models';

/**
 * Client of `MintPlayer.Spark.Search`'s endpoints (`docs/spark_search_PRD.md` D10). All three are anonymous GETs; the
 * server re-reads every hit with the caller's rights, so a hit is always one the caller may open. A burst over the
 * server's per-address budget answers 429.
 *
 * The term goes into the URL through `encodeURIComponent`, as the server's prerender contributor predicts it: the
 * results page's request must equal the supplied one byte for byte, or the browser fetches it again.
 */
@Injectable({ providedIn: 'root' })
export class SparkSearchService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(SPARK_CONFIG, { optional: true })?.baseUrl ?? '/spark';

  /** Suggestions for a term being typed. Unsubscribing cancels the request (a newer keystroke). */
  suggest(term: string): Observable<SparkSearchHit[]> {
    return this.http.get<SparkSearchHit[]>(`${this.baseUrl}/search/suggest?q=${encodeURIComponent(term.trim())}`);
  }

  /** The results of a completed term, best first. */
  search(term: string): Observable<SparkSearchHit[]> {
    return this.http.get<SparkSearchHit[]>(`${this.baseUrl}/search?q=${encodeURIComponent(term.trim())}`);
  }

  /** The one object a picked suggestion or a term names, or null when it names none or several. */
  async resolve(term: string): Promise<SparkSearchHit | null> {
    try {
      return await firstValueFrom(this.http.get<SparkSearchHit>(`${this.baseUrl}/search/resolve?q=${encodeURIComponent(term.trim())}`));
    } catch (error) {
      if (error instanceof HttpErrorResponse && error.status === 404) return null;
      throw error;
    }
  }
}
