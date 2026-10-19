import { TestBed } from '@angular/core/testing';
import { provideHttpClient, HttpErrorResponse } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Subject } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { SparkLanguageService, SparkLiveService, SparkService } from '@mintplayer/ng-spark/services';
import { WaitingRoomEntryView, WaitingRoomInfo } from './generated/contracts';
import { SparkWaitingRoomService, waitingRoomTopic } from './spark-waiting-room.service';
import { SparkWaitingRoomComponent } from './spark-waiting-room.component';
import { sparkWaitingRoomRoutes } from './provide-spark-waiting-room';

// Commerce #521 M16: the client of MintPlayer.Spark.WaitingRoom.

const info = (overrides: Partial<WaitingRoomInfo> = {}): WaitingRoomInfo => ({
  key: 'fest',
  title: null,
  mode: 'RandomizedPreQueue',
  status: 'open',
  opensAt: null,
  startsAt: '2026-10-19T12:00:00Z',
  endsAt: null,
  requireSignIn: true,
  servedUpTo: 10,
  queueLength: 40,
  seedCommitment: 'c'.repeat(64),
  seed: null,
  joinChallenge: false,
  ...overrides,
});

const entry = (overrides: Partial<WaitingRoomEntryView> = {}): WaitingRoomEntryView => ({
  entryId: 'WaitingRoomEntries/1',
  roomKey: 'fest',
  status: 'waiting',
  rank: 25,
  ahead: 14,
  preQueue: true,
  presenceDeadline: null,
  admissionToken: null,
  admissionExpiresAt: null,
  admittedUrl: null,
  misses: 0,
  ...overrides,
});

describe('waiting-room: the service', () => {
  let spark: { postEnvelope: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    spark = { postEnvelope: vi.fn() };
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: SparkService, useValue: spark }],
    });
  });

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('reads the room and the entry, unwrapping the envelope; no entry is null', async () => {
    const service = TestBed.inject(SparkWaitingRoomService);
    const http = TestBed.inject(HttpTestingController);

    const room = service.info('fest');
    http.expectOne('/spark/waiting-room/fest').flush({ result: info() });
    expect((await room)?.mode).toBe('RandomizedPreQueue');

    const mine = service.entry('fest');
    http.expectOne('/spark/waiting-room/fest/entry').flush({}, { status: 404, statusText: 'Not Found' });
    expect(await mine).toBeNull();
  });

  it('answers a join outcome whatever its status code (428 challenge, 403, 429)', async () => {
    const service = TestBed.inject(SparkWaitingRoomService);
    spark.postEnvelope.mockRejectedValueOnce(new HttpErrorResponse({
      status: 428,
      error: { result: { outcome: 'challenge', challenge: { provider: 'altcha', point: 'queue-join', consentFallback: false } } },
    }));
    const result = await service.join('fest', { website: null });
    expect(result.outcome).toBe('challenge');
    expect(spark.postEnvelope).toHaveBeenCalledWith('/waiting-room/fest/join', { website: null });

    spark.postEnvelope.mockResolvedValueOnce({ outcome: 'joined', entry: entry() });
    expect((await service.join('fest')).entry?.rank).toBe(25);

    spark.postEnvelope.mockRejectedValueOnce(new HttpErrorResponse({ status: 500 }));
    await expect(service.join('fest')).rejects.toBeInstanceOf(HttpErrorResponse);
  });

  it('names the live topic after the room', () => {
    expect(waitingRoomTopic('fest')).toBe('waiting-room:fest');
  });
});

describe('waiting-room: the page', () => {
  let rooms: Record<string, ReturnType<typeof vi.fn>>;
  let privateFrames: Subject<unknown>;

  beforeEach(() => {
    privateFrames = new Subject();
    rooms = {
      info: vi.fn().mockResolvedValue(info()),
      entry: vi.fn().mockResolvedValue(entry({ status: 'called', presenceDeadline: '2026-10-19T12:01:00Z' })),
      frames: vi.fn().mockReturnValue(new Subject()),
      privateFrames: vi.fn().mockReturnValue(privateFrames),
      presence: vi.fn().mockResolvedValue(entry({ status: 'admitted', admissionToken: 'tok', admittedUrl: '/buy?admission=tok' })),
      join: vi.fn(),
      leave: vi.fn(),
    };
    TestBed.configureTestingModule({
      imports: [SparkWaitingRoomComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: SparkWaitingRoomService, useValue: rooms },
        { provide: SparkLiveService, useValue: {} },
        { provide: SparkLanguageService, useValue: { t: (key: string) => key } },
      ],
    });
  });

  it('shows a single "I\'m here" action for a called entry and emits the token once admitted', async () => {
    const fixture = TestBed.createComponent(SparkWaitingRoomComponent);
    fixture.componentRef.setInput('key', 'fest');
    const admitted = vi.fn();
    fixture.componentInstance.admitted.subscribe(admitted);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const button = (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('.spark-waiting-room-here');
    expect(button?.textContent).toContain('waitingRoom.imHere');
    button!.click();
    await fixture.whenStable();
    expect(rooms['presence']).toHaveBeenCalledWith('fest', true);
    expect(admitted).toHaveBeenCalledWith({ token: 'tok', url: '/buy?admission=tok' });
    fixture.destroy();
  });

  it('routes :key to the page', () => {
    expect(sparkWaitingRoomRoutes()[0].path).toBe(':key');
  });
});
