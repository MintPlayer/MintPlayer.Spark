import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { SparkService } from '@mintplayer/ng-spark/services';
import { packPlan } from './layout-editor/plan-pack';
import { buildSeatMapModel, TAG_ACCESSIBLE, TAG_COMPANION } from './seat-map/seat-map-model';
import { syntheticStadium } from './seat-map/test-utils';
import { pickerOptions } from './spark-seat-picker.component';
import { seatMapTopic, seatPickable } from './spark-seat-map.component';
import { diffMarks } from './spark-venue-layout-review.component';
import { SparkVenueSaveError, SparkVenuesService } from './spark-venues.service';

// Commerce #521 M14: the ng-spark client of MintPlayer.Spark.Commerce.Ticketing.Venues and the picker's rules.

describe('SparkVenuesService', () => {
  let http: HttpTestingController;
  let service: SparkVenuesService;
  const postEnvelope = vi.fn();

  beforeEach(() => {
    postEnvelope.mockReset();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: SparkService, useValue: { postEnvelope } }],
    });
    http = TestBed.inject(HttpTestingController);
    service = TestBed.inject(SparkVenuesService);
  });

  afterEach(() => http.verify());

  it('reads a seat map with the id in the query string, and null when there is none', async () => {
    const pending = service.seatMap('VenueLayoutSnapshots/abc');
    const request = http.expectOne((r) => r.url === '/spark/ticketing/venues/seatmap');
    expect(request.request.params.get('snapshotId')).toBe('VenueLayoutSnapshots/abc');
    request.flush(null, { status: 404, statusText: 'Not Found' });
    expect(await pending).toBeNull();
  });

  it('turns a 409 into a conflict and a 400 into problems', async () => {
    postEnvelope.mockRejectedValueOnce({ status: 409, error: { result: { revision: 7, problems: [] } } });
    const conflict = await service.saveDraft('VenueLayouts/1-A', 3, packPlanToPlan()).catch((e) => e);
    expect(conflict).toBeInstanceOf(SparkVenueSaveError);
    expect((conflict as SparkVenueSaveError).kind).toBe('conflict');
    expect((conflict as SparkVenueSaveError).revision).toBe(7);

    postEnvelope.mockRejectedValueOnce({ status: 400, error: { result: { revision: 0, problems: [{ path: 'blocks[0]', code: 'ticketingVenues.problem.polygon' }] } } });
    const invalid = (await service.saveDraft('VenueLayouts/1-A', 3, packPlanToPlan()).catch((e) => e)) as SparkVenueSaveError;
    expect(invalid.kind).toBe('invalid');
    expect(invalid.problems[0].code).toBe('ticketingVenues.problem.polygon');
  });

  it('posts saves with the id in the body', async () => {
    postEnvelope.mockResolvedValue({ revision: 4, problems: [] });
    expect(await service.saveDraft('VenueLayouts/1-A', 3, packPlanToPlan())).toBe(4);
    expect(postEnvelope).toHaveBeenCalledWith('/ticketing/venues/layouts/draft', expect.objectContaining({ layoutId: 'VenueLayouts/1-A', revision: 3 }));
  });

  it('escapes ids in URLs', () => {
    expect(service.csvUrl('VenueLayouts/1-A')).toBe('/spark/ticketing/venues/layouts/csv?layoutId=VenueLayouts%2F1-A&version=draft');
    expect(seatMapTopic('VenueLayoutSnapshots/x')).toBe('ticketing-venues:VenueLayoutSnapshots/x');
  });
});

function packPlanToPlan() {
  return { width: 10, height: 10, background: null, categories: [], blocks: [], decor: [], chains: [] };
}

describe('accessible seating in the picker (default 3)', () => {
  const model = buildSeatMapModel(syntheticStadium({ blocksX: 1, blocksY: 1, rows: 2, seatsPerRow: 4 }));
  // test-utils tags seat 0 of the first row accessible and seat 1 companion.

  it('offers a wheelchair space only to a ticket type that allows it', () => {
    expect(model.tags[0] & TAG_ACCESSIBLE).toBeTruthy();
    expect(seatPickable(model, 0, 0, new Set())).toBe(false);
    expect(seatPickable(model, 0, TAG_ACCESSIBLE, new Set())).toBe(true);
    expect(seatPickable(model, 2, 0, new Set())).toBe(true);
  });

  it('lets the companion seat travel with its wheelchair space', () => {
    expect(model.tags[1] & TAG_COMPANION).toBeTruthy();
    expect(seatPickable(model, 1, 0, new Set())).toBe(false);
    expect(seatPickable(model, 1, TAG_ACCESSIBLE, new Set([0]))).toBe(true);
    expect(seatPickable(model, 1, TAG_COMPANION, new Set())).toBe(true);
  });
});

describe('list alternative', () => {
  it('lists chained numbered blocks and unnumbered blocks with what is left', () => {
    const payload = packPlan({
      width: 100, height: 100, background: null, categories: [], decor: [],
      blocks: [
        { key: 'A', name: 'A', kind: 'seatedNumbered', polygon: [0, 0, 1, 0, 1, 1], capacity: 0, category: null, rows: [{ key: 'r', label: '1', seats: [{ key: 's', label: '1', x: 0, y: 0, tags: 0 }] }] },
        { key: 'B', name: 'B', kind: 'seatedNumbered', polygon: [0, 0, 1, 0, 1, 1], capacity: 0, category: null, rows: [{ key: 'r', label: '1', seats: [{ key: 't', label: '1', x: 0, y: 0, tags: 0 }] }] },
        { key: 'P', name: 'Pit', kind: 'standing', polygon: [0, 0, 1, 0, 1, 1], capacity: 50, category: null, rows: [] },
      ],
      chains: [{ key: 'main', name: 'Main', blocks: [{ blockKey: 'A', backToFront: false, reverseSeats: false }] }],
    });
    const options = pickerOptions(payload, { snapshotId: '', seatCount: 2, states: '', blocks: [{ key: 'A', available: 1 }, { key: 'P', available: 12 }] });
    expect(options).toEqual([
      { blockKey: 'A', name: 'A', chainKey: 'main', available: 1, numbered: true },
      { blockKey: 'P', name: 'Pit', chainKey: null, available: 12, numbered: false },
    ]);
  });
});

describe('layout review', () => {
  it('outlines added, moved and changed seats', () => {
    const model = buildSeatMapModel(syntheticStadium({ blocksX: 1, blocksY: 1, rows: 1, seatsPerRow: 3 }));
    const marks = diffMarks(model, {
      addedCount: 1, removedCount: 5, movedCount: 1, changedCount: 0,
      added: ['b0-r0-s0'], removed: ['gone'], moved: ['b0-r0-s2'], changed: [], changedBlocks: [], changedChains: [],
    });
    expect(Array.from(marks)).toEqual([1, 0, 1]);
  });
});
