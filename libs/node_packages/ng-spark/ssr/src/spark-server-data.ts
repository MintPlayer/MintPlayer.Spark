import { InjectionToken } from '@angular/core';

/** One response the .NET supplier produced in-process for a request the page is going to make. */
export interface SparkSuppliedResponse {
  /** {@link sparkServerRequestKey} of the request. */
  key: string;
  status: number;
  /** The response body exactly as the HTTP endpoint would have written it (JSON text), or null. */
  body: string | null;
}

/** What `SparkPrerenderingService.OnSupplyData` hands the render (`params.data.spark`). */
export interface SparkServerData {
  responses: SparkSuppliedResponse[];
  /** The language the request resolved to (`spark-lang` cookie, then Accept-Language, then the default). */
  language: string;
  /** The viewer's IANA zone (`spark-timezone` cookie), or the app's default zone (D10). */
  timeZone: string;
  /** The page's public origin, e.g. `https://mintplayer.com` (canonical and Open Graph URLs, D12). */
  origin: string;
}

/** The request's server data. Provided by the renderer on the server only; absent in the browser. */
export const SPARK_SERVER_DATA = new InjectionToken<SparkServerData>('SPARK_SERVER_DATA');
