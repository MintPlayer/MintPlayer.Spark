import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { SPARK_CONFIG } from '@mintplayer/ng-spark';
import { SparkServicePoint, SparkServicePointKind, SparkShippingRate } from './generated/contracts';

/** A service-point search (GB5): the buyer's shipping address. */
export interface SparkServicePointSearch {
  country: string;
  postalCode: string;
  carrier?: string;
  city?: string;
  street?: string;
  houseNumber?: string;
  kind?: SparkServicePointKind;
  limit?: number;
}

/** A rate question: where to, and the goods subtotal for free-shipping thresholds. */
export interface SparkShippingRateSearch {
  country: string;
  postalCode?: string;
  subtotal?: number;
  weightGrams?: number;
}

/**
 * Client for `MintPlayer.Spark.Commerce.Shipping`' endpoints (`/spark/commerce/shipping/*`, commerce #521 M11).
 * Ids travel in query strings, never in paths (RavenDB ids contain slashes).
 */
@Injectable({ providedIn: 'root' })
export class SparkCommerceShippingService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(SPARK_CONFIG, { optional: true })?.baseUrl ?? '/spark';

  /** `GET /spark/commerce/shipping/service-points`: the points and lockers nearest an address (GB5), nearest first. */
  async servicePoints(search: SparkServicePointSearch): Promise<SparkServicePoint[]> {
    const params: Record<string, string> = { country: search.country, postalCode: search.postalCode };
    if (search.carrier) params['carrier'] = search.carrier;
    if (search.city) params['city'] = search.city;
    if (search.street) params['street'] = search.street;
    if (search.houseNumber) params['houseNumber'] = search.houseNumber;
    if (search.kind) params['kind'] = search.kind;
    if (search.limit) params['limit'] = String(search.limit);
    const envelope = await firstValueFrom(this.http.get<{ result?: SparkServicePoint[] } | null>(
      `${this.baseUrl}/commerce/shipping/service-points`, { params }));
    return envelope?.result ?? [];
  }

  /**
   * `GET /spark/commerce/shipping/rates`: the methods a destination is offered, with thresholds, the customs choice
   * (GB6) and delivery promises (GC11). Zones are keyed on the delivery country, which geo-blocking rules allow (GA11).
   */
  async rates(search: SparkShippingRateSearch): Promise<SparkShippingRate[]> {
    const params: Record<string, string> = { country: search.country };
    if (search.postalCode) params['postalCode'] = search.postalCode;
    if (search.subtotal !== undefined) params['subtotal'] = String(search.subtotal);
    if (search.weightGrams !== undefined) params['weight'] = String(Math.max(0, Math.round(search.weightGrams)));
    const envelope = await firstValueFrom(this.http.get<{ result?: SparkShippingRate[] } | null>(
      `${this.baseUrl}/commerce/shipping/rates`, { params }));
    return envelope?.result ?? [];
  }

  /** The URL of a parcel's label (PDF or ZPL), for a link or the print path. Staff only (the server checks). */
  labelUrl(shipmentId: string, parcel: number): string {
    const query = new URLSearchParams({ id: shipmentId, parcel: String(parcel) });
    return `${this.baseUrl}/commerce/shipping/label?${query}`;
  }

  /** The URL of a shipment's customs document (GB6). Staff only. */
  customsDocumentUrl(shipmentId: string): string {
    return `${this.baseUrl}/commerce/shipping/customs-document?${new URLSearchParams({ id: shipmentId })}`;
  }
}
