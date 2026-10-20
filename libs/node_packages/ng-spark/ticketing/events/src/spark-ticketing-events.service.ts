import { inject, Injectable } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { SPARK_CONFIG } from '@mintplayer/ng-spark';
import { SparkService } from '@mintplayer/ng-spark/services';
import {
  SparkAttendeeAnswersRequest,
  SparkAttendeeQuestionnaire,
  SparkEventAvailability,
  SparkPreSelection,
  SparkPreSelectionRequest,
  SparkPublicEvent,
  SparkPublicEventList,
  SparkQuickEventRequest,
  SparkQuickEventResult,
  SparkTicketingRefusal,
} from './generated/contracts';

/** The item-id prefix of ticketing (checkout's `itemId`). */
export const TICKETING_ITEM_PREFIX = 'ticketing:';

/** The date segment of a multi-day pass. */
export const TICKETING_PASS_DATE = 'pass';

/**
 * The checkout item id of a ticket: `ticketing:{phaseId}|{ticketTypeId}|{category}|{dateId}` (or `|pass`), the same
 * form `TicketItemIds` builds on the server. Ids travel in bodies, never in paths.
 */
export function ticketItemId(phaseId: string, ticketTypeId: string, category: string | null | undefined, dateId: string): string {
  return `${TICKETING_ITEM_PREFIX}${phaseId}|${ticketTypeId}|${category ?? ''}|${dateId}`;
}

/** The parts of a ticketing item id, or `null` for any other item. */
export function parseTicketItemId(itemId: string): { phaseId: string; ticketTypeId: string; category: string; dateId: string } | null {
  if (!itemId.startsWith(TICKETING_ITEM_PREFIX)) return null;
  const parts = itemId.slice(TICKETING_ITEM_PREFIX.length).split('|');
  if (parts.length !== 4 || !parts[0] || !parts[1] || !parts[3]) return null;
  return { phaseId: parts[0], ticketTypeId: parts[1], category: parts[2], dateId: parts[3] };
}

/**
 * The refusal a ticketing endpoint answered (HTTP 409 whose envelope result is a {@link SparkTicketingRefusal}), or
 * `null` for any other error. Checkout's own refusals (sold out, limit) come back in the same shape.
 */
export function ticketingRefusalOf(error: unknown): SparkTicketingRefusal | null {
  if (!(error instanceof HttpErrorResponse) || error.status !== 409) return null;
  const body = error.error as { result?: unknown } | null | undefined;
  const candidate = body && typeof body === 'object' && 'result' in body ? body.result : body;
  return candidate && typeof candidate === 'object' && typeof (candidate as SparkTicketingRefusal).code === 'string'
    ? (candidate as SparkTicketingRefusal)
    : null;
}

/** The translation key of a refusal: ticketing's own codes, else checkout's. */
export function ticketingRefusalKey(code: string): string {
  const own = ['eventNotFound', 'unknownChannel', 'invalidEvent', 'notPublishable', 'forbidden', 'answersRefused', 'answersClosed'];
  return own.includes(code) ? 'ticketingEvents.refusal.' + code : 'commerceCheckout.refusal.' + code;
}

/**
 * Client for `MintPlayer.Spark.Commerce.Ticketing.Events`' endpoints (`/spark/ticketing/events/*`, commerce #521 M13).
 * Reads are GETs whose answer is the add-on envelope (`{ result }`); POSTs go through {@link SparkService.postEnvelope}
 * so the app's XSRF header goes with them. Putting tickets in the basket is checkout's
 * (`SparkCommerceCheckoutService.add` with {@link ticketItemId}).
 */
@Injectable({ providedIn: 'root' })
export class SparkTicketingEventsService {
  private readonly spark = inject(SparkService);
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(SPARK_CONFIG, { optional: true })?.baseUrl ?? '/spark';

  private url(path: string): string {
    return `${this.baseUrl}/ticketing/events${path}`;
  }

  private async get<T>(path: string, params: Record<string, string | null | undefined>): Promise<T | null> {
    let httpParams = new HttpParams();
    for (const [key, value] of Object.entries(params)) {
      if (value != null && value !== '') httpParams = httpParams.set(key, value);
    }
    const envelope = await firstValueFrom(this.http.get<{ result?: T } | null>(this.url(path), { params: httpParams }));
    return envelope?.result ?? null;
  }

  /** `GET …/public?slug=`: a published event's page data, or `null`. */
  publicEvent(slug: string): Promise<SparkPublicEvent | null> {
    return this.get<SparkPublicEvent>('/public', { slug });
  }

  /** `GET …/public/list?skip=&take=`: the storefront's event list, upcoming first (M17). */
  async publicEvents(skip = 0, take = 24): Promise<SparkPublicEventList> {
    return (await this.get<SparkPublicEventList>('/public/list', { skip: String(skip), take: String(take) }))
      ?? { events: [], total: 0, skip, take };
  }

  /** `GET …/availability?eventId=&phaseId=`: places left per date (the waiting page's too, §4.11). */
  availability(eventId: string, phaseId?: string | null): Promise<SparkEventAvailability | null> {
    return this.get<SparkEventAvailability>('/availability', { eventId, phaseId });
  }

  /** The URL of a date's `.ics` file (GB9), for a plain download link. */
  calendarUrl(dateId: string, culture?: string | null): string {
    const params = new HttpParams({ fromObject: culture ? { dateId, culture } : { dateId } });
    return `${this.url('/calendar')}?${params.toString()}`;
  }

  /** `POST …/quick`: a one-off event on one screen (§4.11). */
  quickCreate(request: SparkQuickEventRequest): Promise<SparkQuickEventResult> {
    return this.spark.postEnvelope<SparkQuickEventResult>('/ticketing/events/quick', { ...request });
  }

  /** `GET …/attendees?number=`: the attendee questions of the caller's own order (GB3). */
  attendeeQuestions(orderNumber: string): Promise<SparkAttendeeQuestionnaire | null> {
    return this.get<SparkAttendeeQuestionnaire>('/attendees', { number: orderNumber });
  }

  /** `POST …/attendees`: one ticket position's answers. */
  saveAnswers(request: SparkAttendeeAnswersRequest): Promise<SparkAttendeeQuestionnaire> {
    return this.spark.postEnvelope<SparkAttendeeQuestionnaire>('/ticketing/events/attendees', { ...request });
  }

  /** `GET …/preselect?phaseId=`: the caller's pre-selection (D25). */
  preSelection(phaseId: string): Promise<SparkPreSelection | null> {
    return this.get<SparkPreSelection>('/preselect', { phaseId });
  }

  /** `POST …/preselect`: saves the pre-selection. */
  savePreSelection(request: SparkPreSelectionRequest): Promise<SparkPreSelection> {
    return this.spark.postEnvelope<SparkPreSelection>('/ticketing/events/preselect', { ...request });
  }

  /** `POST …/preselect/apply`: at admission, with the waiting room's token. */
  applyPreSelection(phaseId: string, admissionToken: string | null): Promise<SparkPreSelection> {
    return this.spark.postEnvelope<SparkPreSelection>('/ticketing/events/preselect/apply', { phaseId, admissionToken });
  }

  /** `POST …/preselect/release`: one click gives every held line back. */
  releasePreSelection(phaseId: string): Promise<SparkPreSelection> {
    return this.spark.postEnvelope<SparkPreSelection>('/ticketing/events/preselect/release', { phaseId });
  }
}
