import { HttpBackend, HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, debounceTime, firstValueFrom, map, of, shareReplay, switchMap } from 'rxjs';
import { REFERENCE_DATA_CONFIG } from './reference-data.token';
import {
  CountryProfileResponse,
  StreetSuggestResponse,
  SuggestResponse,
  TimeZonesResponse,
} from './generated/contracts';

export interface SuggestOptions {
  country?: string | null;
  lang?: string | null;
  /** The viewer's local calendar date (`yyyy-MM-dd`); the server defaults to the UTC date. No zone arithmetic (K10). */
  asOf?: string | null;
  limit?: number;
}

/** How a typeahead box queries (see {@link ReferenceDataApi.typeahead}). */
export interface TypeaheadOptions {
  /** Quiet time before a query is sent; every keystroke inside it restarts the wait. Default 200 ms. */
  debounceMs?: number;
  /** Shorter queries answer an empty list without a request. Default 2 (the API's own minimum). */
  minLength?: number;
}

export interface StreetOptions {
  municipality?: string | null;
  postalCode?: string | null;
  lang?: string | null;
  limit?: number;
}

/**
 * The `/api/v1` client (PRD §4.15). Requests go through `new HttpClient(inject(HttpBackend))`, which bypasses every
 * interceptor: no bearer or cookie logic towards a public origin, and no sign-in redirect on an error. The language
 * is an explicit `lang` parameter. In free-text mode (`baseUrl: null`) every call answers `null` without a request.
 */
@Injectable({ providedIn: 'root' })
export class ReferenceDataApi {
  private readonly config = inject(REFERENCE_DATA_CONFIG);
  private readonly http = new HttpClient(inject(HttpBackend));
  private readonly profiles = new Map<string, Observable<CountryProfileResponse | null>>();

  /** Whether an instance is configured (otherwise: free-text mode). */
  get enabled(): boolean {
    return !!this.config.baseUrl;
  }

  suggest(q: string, options: SuggestOptions = {}): Observable<SuggestResponse | null> {
    return this.get<SuggestResponse>('suggest', { q, country: options.country, lang: options.lang, asOf: options.asOf, limit: options.limit });
  }

  streets(q: string, options: StreetOptions): Observable<StreetSuggestResponse | null> {
    return this.get<StreetSuggestResponse>('streets/suggest', {
      q, municipality: options.municipality, postalCode: options.postalCode, lang: options.lang, limit: options.limit,
    });
  }

  /**
   * A typeahead feed (PRD §4.15, RX5): `queries` is what the viewer types; the result is the suggestions for the latest
   * settled query. Debounced, and a newer query cancels the request of an older one (`switchMap` unsubscribes, which
   * aborts the HTTP request), so a slow answer to "kru" never overwrites the answer to "kruish". Every settled query
   * emits, an empty list included, so a box showing "searching" always ends. An unreachable instance answers an empty
   * list: free text stays possible.
   */
  typeahead<T>(queries: Observable<string>, search: (q: string) => Observable<readonly T[] | null | undefined>, options: TypeaheadOptions = {}): Observable<T[]> {
    const minLength = options.minLength ?? 2;
    return queries.pipe(
      map(q => q.trim()),
      debounceTime(options.debounceMs ?? 200),
      switchMap(q => q.length < minLength ? of([] as T[]) : search(q).pipe(map(items => [...(items ?? [])]))),
    );
  }

  /** A country's profile, fetched once per session. */
  profile(country: string): Observable<CountryProfileResponse | null> {
    const key = country.toLowerCase();
    let profile = this.profiles.get(key);
    if (!profile) {
      profile = this.get<CountryProfileResponse>(`countries/${encodeURIComponent(key)}/profile`, {}).pipe(shareReplay(1));
      this.profiles.set(key, profile);
    }
    return profile;
  }

  /** IANA id → CLDR exemplar city in `language`; empty in free-text mode or when the instance is unreachable. */
  async timeZoneNames(language: string): Promise<Record<string, string>> {
    const response = await firstValueFrom(this.get<TimeZonesResponse>('timezones', { lang: language }));
    return response?.names ?? {};
  }

  private get<T>(path: string, query: Record<string, string | number | null | undefined>): Observable<T | null> {
    if (!this.config.baseUrl) return of(null);
    let params = new HttpParams();
    for (const [key, value] of Object.entries(query)) {
      if (value !== null && value !== undefined && value !== '') params = params.set(key, String(value));
    }
    return this.http.get<T>(`${this.config.baseUrl}/api/v1/${path}`, { params }).pipe(
      map(response => response ?? null),
      // An unreachable instance is never a dead end: the form falls back to free text.
      catchError(() => of(null)),
    );
  }
}
