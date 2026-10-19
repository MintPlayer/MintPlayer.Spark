import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { SparkService } from '@mintplayer/ng-spark/services';
import {
  parseTicketItemId,
  SparkTicketingEventsService,
  ticketingRefusalKey,
  ticketingRefusalOf,
  ticketItemId,
} from './spark-ticketing-events.service';

// Commerce #521 M13: the ng-spark client of MintPlayer.Spark.Commerce.Ticketing.Events.

describe('ticket item ids', () => {
  it('build and parse the server form', () => {
    const id = ticketItemId('TicketSalePhases/1-A', 'TicketTypes/2-A', 'cat1', 'TicketEventDates/3-A');
    expect(id).toBe('ticketing:TicketSalePhases/1-A|TicketTypes/2-A|cat1|TicketEventDates/3-A');
    expect(parseTicketItemId(id)).toEqual({ phaseId: 'TicketSalePhases/1-A', ticketTypeId: 'TicketTypes/2-A', category: 'cat1', dateId: 'TicketEventDates/3-A' });
    expect(ticketItemId('p', 't', null, 'pass')).toBe('ticketing:p|t||pass');
    expect(parseTicketItemId('Products/1-A')).toBeNull();
    expect(parseTicketItemId('ticketing:p|t|c')).toBeNull();
  });
});

describe('ticketing refusals', () => {
  it('reads a 409 envelope and keys own and checkout codes', () => {
    const error = new HttpErrorResponse({ status: 409, error: { result: { code: 'answersRefused', errors: [{ key: 'name', code: 'required' }] } } });
    expect(ticketingRefusalOf(error)?.errors[0].code).toBe('required');
    expect(ticketingRefusalOf(new HttpErrorResponse({ status: 500 }))).toBeNull();
    expect(ticketingRefusalKey('answersRefused')).toBe('ticketingEvents.refusal.answersRefused');
    expect(ticketingRefusalKey('soldOut')).toBe('commerceCheckout.refusal.soldOut');
  });
});

describe('SparkTicketingEventsService', () => {
  let http: HttpTestingController;
  let service: SparkTicketingEventsService;
  const postEnvelope = vi.fn();

  beforeEach(() => {
    postEnvelope.mockReset();
    postEnvelope.mockResolvedValue({});
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: SparkService, useValue: { postEnvelope } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    service = TestBed.inject(SparkTicketingEventsService);
  });

  afterEach(() => http.verify());

  it('reads the public event and the availability with query strings, never ids in paths', async () => {
    const ev = service.publicEvent('summer-fest');
    http.expectOne((r) => r.method === 'GET' && r.url === '/spark/ticketing/events/public' && r.params.get('slug') === 'summer-fest')
      .flush({ result: { id: 'TicketEvents/1-A', slug: 'summer-fest', dates: [] } });
    expect((await ev)?.id).toBe('TicketEvents/1-A');

    const availability = service.availability('TicketEvents/1-A');
    const request = http.expectOne((r) => r.url === '/spark/ticketing/events/availability');
    expect(request.request.params.get('eventId')).toBe('TicketEvents/1-A');
    expect(request.request.params.has('phaseId')).toBe(false);
    request.flush({ result: { eventId: 'TicketEvents/1-A', dates: [] } });
    expect((await availability)?.dates).toEqual([]);
  });

  it('posts answers and pre-selections through the XSRF-carrying envelope', async () => {
    await service.saveAnswers({ orderNumber: 'T-1', lineId: 'l1', position: 1, answers: [{ questionKey: 'name', value: 'Ann', consent: false }] });
    expect(postEnvelope).toHaveBeenCalledWith('/ticketing/events/attendees', expect.objectContaining({ orderNumber: 'T-1', position: 1 }));
    await service.applyPreSelection('p1', 'token');
    expect(postEnvelope).toHaveBeenCalledWith('/ticketing/events/preselect/apply', { phaseId: 'p1', admissionToken: 'token' });
    await service.releasePreSelection('p1');
    expect(postEnvelope).toHaveBeenCalledWith('/ticketing/events/preselect/release', { phaseId: 'p1' });
  });

  it('links the .ics of a date with its culture', () => {
    expect(service.calendarUrl('TicketEventDates/1-A', 'nl')).toBe('/spark/ticketing/events/calendar?dateId=TicketEventDates/1-A&culture=nl');
  });
});
