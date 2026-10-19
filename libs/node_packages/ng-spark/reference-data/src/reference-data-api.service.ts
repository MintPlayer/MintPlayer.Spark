import { HttpBackend, HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, firstValueFrom, map, of, shareReplay } from 'rxjs';
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
