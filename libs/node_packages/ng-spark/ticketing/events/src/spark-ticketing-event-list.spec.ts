import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { SparkService } from '@mintplayer/ng-spark/services';
import { SparkTicketingEventsService } from './spark-ticketing-events.service';
import { eventPageUrl } from './spark-ticketing-event-list.component';

// M17: the storefront's event list.

describe('event list', () => {
  let http: HttpTestingController;
  let service: SparkTicketingEventsService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: SparkService, useValue: {} }],
    });
    http = TestBed.inject(HttpTestingController);
    service = TestBed.inject(SparkTicketingEventsService);
  });

  afterEach(() => http.verify());

  it('pages the public list through the query string', async () => {
    const page = service.publicEvents(24, 12);
    const req = http.expectOne((r) => r.url === '/spark/ticketing/events/public/list');
    expect(req.request.params.get('skip')).toBe('24');
    expect(req.request.params.get('take')).toBe('12');
    req.flush({ result: { events: [], total: 30, skip: 24, take: 12 } });
    expect((await page).total).toBe(30);
  });

  it('answers an empty page when the envelope is empty', async () => {
    const page = service.publicEvents();
    http.expectOne((r) => r.url === '/spark/ticketing/events/public/list').flush(null);
    expect(await page).toEqual({ events: [], total: 0, skip: 0, take: 24 });
  });

  it('builds the event page URL from the server pattern, escaping the slug', () => {
    expect(eventPageUrl('/events/{slug}', 'rock werchter')).toBe('/events/rock%20werchter');
  });
});
