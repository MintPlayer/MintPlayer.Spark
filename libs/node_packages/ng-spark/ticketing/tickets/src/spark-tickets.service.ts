import { inject, Injectable } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { SPARK_CONFIG } from '@mintplayer/ng-spark';
import { SparkService } from '@mintplayer/ng-spark/services';
import type { Money } from '@mintplayer/ng-spark/models';
import {
  SparkResaleOffers,
  SparkScannerDeviceRegistered,
  SparkScannerDeviceRequest,
  SparkTicketCredential,
  SparkTicketList,
  SparkTicketRefusal,
  SparkTicketResaleInfo,
  SparkTicketResendResult,
  SparkTicketTransferInfo,
  SparkTicketTransferOffer,
  SparkTicketView,
} from './generated/contracts';

/**
 * The refusal a tickets endpoint answered (HTTP 409 whose envelope result is a {@link SparkTicketRefusal}), or `null`.
 * Checkout's refusals (a resale listing sold meanwhile) come back in the same shape.
 */
export function ticketRefusalOf(error: unknown): SparkTicketRefusal | null {
  if (!(error instanceof HttpErrorResponse) || error.status !== 409) return null;
  const body = error.error as { result?: unknown } | null | undefined;
  const candidate = body && typeof body === 'object' && 'result' in body ? body.result : body;
  return candidate && typeof candidate === 'object' && typeof (candidate as SparkTicketRefusal).code === 'string' ? (candidate as SparkTicketRefusal) : null;
}

const OWN_REFUSALS = [
  'notFound', 'forbidden', 'notDelivered', 'revoked', 'onHold', 'frozen', 'transferNotAllowed', 'resaleNotAllowed', 'aboveFaceValue',
  'alreadyListed', 'listingClosed', 'transferPending', 'transferClosed', 'nameRequired', 'emailRequired', 'feeUnpaid', 'invalidSettings',
  'deviceRevoked', 'listNotFound', 'tooManyScans',
];

/** The translation key of a refusal: the tickets package's own codes, else checkout's. */
export function ticketRefusalKey(code: string): string {
  return OWN_REFUSALS.includes(code) ? 'ticketingTickets.refusal.' + code : 'commerceCheckout.refusal.' + code;
}

/**
 * Reads a ticket link: `/tickets/{code}#k={secret}`. The secret lives in the fragment, which browsers never send to a
 * server, and travels only in POST bodies.
 */
export function ticketCredentialFromLink(code: string, fragment: string | null | undefined): SparkTicketCredential {
  const secret = new URLSearchParams((fragment ?? '').replace(/^#/, '')).get('k');
  return { code: code.toUpperCase(), secret: secret || null };
}

/** Reads a transfer link's token: `/tickets/transfer/{id}#t={token}`. */
export function transferTokenFromLink(fragment: string | null | undefined): string | null {
  return new URLSearchParams((fragment ?? '').replace(/^#/, '')).get('t') || null;
}

/**
 * The currency a resale is listed in: the open listing's, else the ceiling the server sends before any listing exists
 * (what the ticket cost, in the currency the event's order was paid in). `null` when neither is known: the form waits
 * instead of guessing a currency the server would refuse.
 */
export function resaleCurrency(ticket: Pick<SparkTicketView, 'resale' | 'resaleCap'> | null | undefined): string | null {
  return ticket?.resale?.cap?.Currency ?? ticket?.resaleCap?.Currency ?? null;
}

/**
 * Client for `MintPlayer.Spark.Commerce.Ticketing.Tickets`' holder and seller endpoints (`/spark/ticketing/tickets/*`,
 * commerce #521 M15). A ticket's secret is sent only in POST bodies; POSTs go through {@link SparkService.postEnvelope}
 * so the app's XSRF header goes with them. The door device has its own client (`SparkTicketScannerClient`).
 */
@Injectable({ providedIn: 'root' })
export class SparkTicketsService {
  private readonly spark = inject(SparkService);
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(SPARK_CONFIG, { optional: true })?.baseUrl ?? '/spark';

  private url(path: string): string {
    return `${this.baseUrl}/ticketing/tickets${path}`;
  }

  /** `GET …/mine`: the tickets the signed-in account (or the guest visitor) holds. */
  async mine(): Promise<SparkTicketList> {
    const envelope = await firstValueFrom(this.http.get<{ result?: SparkTicketList } | null>(this.url('/mine')));
    return envelope?.result ?? { tickets: [] };
  }

  /** `POST …/open`: one ticket for its holder. */
  open(credential: SparkTicketCredential): Promise<SparkTicketView> {
    return this.spark.postEnvelope<SparkTicketView>('/ticketing/tickets/open', { ...credential });
  }

  /** `POST …/pdf`: the ticket's PDF (GC7 template). */
  pdf(credential: SparkTicketCredential): Promise<Blob> {
    return firstValueFrom(this.http.post(this.url('/pdf'), credential, { responseType: 'blob' }));
  }

  /** `POST …/holder`: names the holder of a named ticket (D30). */
  setHolder(credential: SparkTicketCredential, name: string): Promise<SparkTicketView> {
    return this.spark.postEnvelope<SparkTicketView>('/ticketing/tickets/holder', { ...credential, name });
  }

  /** `POST …/transfer`: offers the ticket to `email` (D30). */
  transfer(credential: SparkTicketCredential, email: string, name?: string | null): Promise<SparkTicketTransferInfo> {
    return this.spark.postEnvelope<SparkTicketTransferInfo>('/ticketing/tickets/transfer', { ...credential, email, name: name ?? null });
  }

  /** `POST …/transfer/cancel`. */
  cancelTransfer(credential: SparkTicketCredential): Promise<unknown> {
    return this.spark.postEnvelope('/ticketing/tickets/transfer/cancel', { ...credential });
  }

  /** `POST …/transfer/offer`: what the recipient sees before accepting. */
  transferOffer(transferId: string, token: string): Promise<SparkTicketTransferOffer> {
    return this.spark.postEnvelope<SparkTicketTransferOffer>('/ticketing/tickets/transfer/offer', { transferId, token });
  }

  /** `POST …/transfer/accept`: takes the ticket over; answers it with its new secret. */
  acceptTransfer(transferId: string, token: string, name?: string | null, email?: string | null): Promise<SparkTicketView> {
    return this.spark.postEnvelope<SparkTicketView>('/ticketing/tickets/transfer/accept', { transferId, token, name: name ?? null, email: email ?? null });
  }

  /** `POST …/resale`: lists the ticket at face value or less (D30). */
  resell(credential: SparkTicketCredential, price: Money): Promise<SparkTicketResaleInfo> {
    return this.spark.postEnvelope<SparkTicketResaleInfo>('/ticketing/tickets/resale', { ...credential, price });
  }

  /** `POST …/resale/withdraw`. */
  withdrawResale(credential: SparkTicketCredential): Promise<unknown> {
    return this.spark.postEnvelope('/ticketing/tickets/resale/withdraw', { ...credential });
  }

  /** `GET …/resale/offers?eventId=`: a published event's open listings; their `itemId` goes into checkout's basket. */
  async resaleOffers(eventId: string): Promise<SparkResaleOffers | null> {
    const envelope = await firstValueFrom(this.http.get<{ result?: SparkResaleOffers } | null>(this.url('/resale/offers'), { params: new HttpParams().set('eventId', eventId) }));
    return envelope?.result ?? null;
  }

  /** `POST …/resend`: re-sends an order's tickets, or one ticket (GB7, the seller's members). */
  resend(request: { orderId?: string | null; code?: string | null }): Promise<SparkTicketResendResult> {
    return this.spark.postEnvelope<SparkTicketResendResult>('/ticketing/tickets/resend', { ...request });
  }

  /** `POST …/scanner/devices`: registers a door device; the token is shown once (the `scanner` role). */
  registerDevice(request: SparkScannerDeviceRequest): Promise<SparkScannerDeviceRegistered> {
    return this.spark.postEnvelope<SparkScannerDeviceRegistered>('/ticketing/tickets/scanner/devices', { ...request });
  }

  /** `POST …/scanner/devices/revoke`: the device wipes its list on its next call. */
  revokeDevice(deviceId: string): Promise<unknown> {
    return this.spark.postEnvelope('/ticketing/tickets/scanner/devices/revoke', { deviceId });
  }

  /** The URL of an event's ticket logo or background (GC7). */
  templateImageUrl(eventId: string, kind: 'logo' | 'background'): string {
    return `${this.url('/template-image')}?${new HttpParams({ fromObject: { eventId, kind } }).toString()}`;
  }
}
