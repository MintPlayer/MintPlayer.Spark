import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { Subscription } from 'rxjs';
import { SparkChallengeComponent, SparkChallengeOffer, SparkChallengeResponse } from '@mintplayer/ng-spark/challenges';
import { SparkDateTimePipe } from '@mintplayer/ng-spark/date-time';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SparkTranslatedStringComponent } from '@mintplayer/ng-spark/translated-string';
import { WaitingRoomEntryView, WaitingRoomFrame, WaitingRoomInfo, WaitingRoomPrivateFrame } from './generated/contracts';
import { SparkWaitingRoomService } from './spark-waiting-room.service';

/** How often an open page tells the server it is still there (and whether it is visible). */
const HEARTBEAT_MS = 30_000;

/**
 * A waiting room page (commerce #521 M16, §4.12): join, follow the position live, answer the presence check, continue
 * when admitted. Replaceable per route (D27).
 *
 * Accessibility (GA10, WCAG 2.2.1 Timing Adjustable): the presence check **answers itself while the tab is visible**, so
 * a visitor who is looking needs to do nothing; a hidden tab gets a notification (when the visitor allowed them) and a
 * single "I'm here" button with at least 20 s (the server's window); missing it never costs the place (the entry rejoins
 * at its rank on its next sign of life). The queue position is informational and announced politely, not every second.
 * All tabs of the browser share one entry (the server keys it on the identity); `BroadcastChannel` tells the other tabs
 * at once when one is admitted (K11).
 */
@Component({
  selector: 'spark-waiting-room',
  imports: [TranslateKeyPipe, SparkDateTimePipe, SparkChallengeComponent, SparkTranslatedStringComponent],
  templateUrl: './spark-waiting-room.component.html',
  styleUrl: './spark-waiting-room.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkWaitingRoomComponent implements OnInit {
  private readonly rooms = inject(SparkWaitingRoomService);
  private readonly lang = inject(SparkLanguageService);
  private readonly destroyRef = inject(DestroyRef);
  private subscriptions = new Subscription();
  private channel?: BroadcastChannel;
  private heartbeat?: ReturnType<typeof setInterval>;
  private lastAnnounced = -1;

  /** The room key (route parameter `:key` with `withComponentInputBinding()`). */
  readonly key = input.required<string>();

  /** Emitted once admitted, with the single-use admission token (send it as `X-Spark-Admission`). */
  readonly admitted = output<{ token: string; url: string | null }>();

  protected readonly info = signal<WaitingRoomInfo | null>(null);
  protected readonly entry = signal<WaitingRoomEntryView | null>(null);
  protected readonly frame = signal<WaitingRoomFrame | null>(null);
  protected readonly challenge = signal<SparkChallengeOffer | null>(null);
  protected readonly outcome = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly honeypot = signal('');
  protected readonly announcement = signal('');

  protected readonly status = computed(() => this.frame()?.status ?? this.info()?.status ?? null);

  /** People ahead: from the entry's rank and the live `servedUpTo`. */
  protected readonly ahead = computed(() => {
    const entry = this.entry();
    if (!entry || entry.rank == null) return null;
    if (entry.status === 'called' || entry.status === 'admitted') return 0;
    const served = this.frame()?.servedUpTo ?? this.info()?.servedUpTo ?? 0;
    return Math.max(0, entry.rank - served - 1);
  });

  protected readonly scheduleText = computed(() => {
    const info = this.info();
    if (!info) return '';
    const key = info.mode === 'Ballot' ? 'waitingRoom.status.ballot' : info.mode === 'Fifo' ? 'waitingRoom.status.preQueueFifo' : 'waitingRoom.status.preQueue';
    return key;
  });

  constructor() {
    this.destroyRef.onDestroy(() => this.stop());
    // Announce the position politely when it changes by a step worth hearing (10 %, or the last ten), not every frame.
    effect(() => {
      const ahead = this.ahead();
      if (ahead == null) return;
      untracked(() => {
        const last = this.lastAnnounced;
        if (last < 0 || ahead <= 10 || Math.abs(last - ahead) >= Math.max(1, Math.round(last / 10))) {
          this.lastAnnounced = ahead;
          this.announcement.set(ahead === 0 ? this.lang.t('waitingRoom.aheadNone') : this.lang.t('waitingRoom.ahead').replace('{0}', String(ahead)));
        }
      });
    });
  }

  ngOnInit(): void {
    void this.load();
  }

  protected async join(response?: SparkChallengeResponse): Promise<void> {
    this.busy.set(true);
    this.outcome.set(null);
    try {
      const result = await this.rooms.join(this.key(), {
        challenge: response ?? null,
        website: this.honeypot() || null,
        webdriver: (globalThis.navigator as Navigator & { webdriver?: boolean })?.webdriver === true ? true : null,
      });
      if (result.outcome === 'joined' && result.entry) {
        this.challenge.set(null);
        this.setEntry(result.entry);
      } else if (result.outcome === 'challenge') {
        this.challenge.set(result.challenge ?? null);
      } else {
        this.outcome.set(this.outcomeText(result.outcome, result.retryAfterSeconds, result.decisionId));
      }
    } finally {
      this.busy.set(false);
    }
  }

  /** "I'm here": the single action that answers a check (or brings a missed entry back). */
  protected async present(): Promise<void> {
    const entry = await this.rooms.presence(this.key(), true);
    if (entry) this.setEntry(entry);
  }

  protected async leave(): Promise<void> {
    await this.rooms.leave(this.key());
    this.stop();
    this.entry.set(null);
  }

  protected outcomeText(outcome: string, retryAfter?: number | null, decisionId?: string | null): string {
    return this.lang.t(`waitingRoom.outcome.${outcome}`).replace('{0}', String(retryAfter ?? decisionId ?? ''));
  }

  private async load(): Promise<void> {
    const key = this.key();
    this.info.set(await this.rooms.info(key));
    this.subscriptions.add(this.rooms.frames(key).subscribe(frame => this.frame.set(frame)));
    const entry = await this.rooms.entry(key);
    if (entry) this.setEntry(entry);
  }

  private setEntry(entry: WaitingRoomEntryView): void {
    const previous = this.entry();
    const first = !previous;
    this.entry.set(entry);
    if (entry.status === 'admitted' && entry.admissionToken && previous?.admissionToken !== entry.admissionToken) {
      this.admitted.emit({ token: entry.admissionToken, url: entry.admittedUrl ?? null });
      this.channel?.postMessage({ kind: 'admitted', entryId: entry.entryId });
    }
    if (first) this.follow();
  }

  /** Once there is an entry: the private stream, the heartbeat, visibility, the other tabs. */
  private follow(): void {
    const key = this.key();
    this.subscriptions.add(this.rooms.privateFrames(key).subscribe(frame => void this.onPrivate(frame)));
    if (typeof BroadcastChannel !== 'undefined') {
      this.channel = new BroadcastChannel(`spark-waiting-room:${key}`);
      this.channel.onmessage = () => void this.rooms.entry(key).then(e => e && this.entry.set(e));
    }
    const onVisibility = () => void this.signOfLife();
    document.addEventListener('visibilitychange', onVisibility);
    this.subscriptions.add(() => document.removeEventListener('visibilitychange', onVisibility));
    this.heartbeat = setInterval(() => void this.signOfLife(), HEARTBEAT_MS);
  }

  private async onPrivate(frame: WaitingRoomPrivateFrame): Promise<void> {
    if (frame.kind === 'called') {
      const current = this.entry();
      if (current) this.entry.set({ ...current, status: 'called', presenceDeadline: frame.deadline ?? null });
      if (document.visibilityState === 'visible') await this.signOfLife();
      else this.notifyTurn();
      return;
    }
    const entry = await this.rooms.entry(this.key());
    if (entry) this.setEntry(entry);
  }

  /** Visible: answers a check by itself. Hidden: only tells the server (a hidden tab gets the long window). */
  private async signOfLife(): Promise<void> {
    if (!this.entry()) return;
    const entry = await this.rooms.presence(this.key(), document.visibilityState === 'visible');
    if (entry) this.setEntry(entry);
  }

  /** A notification for a hidden tab, only when the visitor already allowed them (no permission prompt here). */
  private notifyTurn(): void {
    if (typeof Notification === 'undefined' || Notification.permission !== 'granted') return;
    try {
      new Notification(this.lang.t('waitingRoom.title'), { body: this.lang.t('waitingRoom.imHere'), tag: `spark-waiting-room:${this.key()}` });
    } catch {
      // Some browsers only allow notifications from a service worker; the page's prompt still shows.
    }
  }

  private stop(): void {
    this.subscriptions.unsubscribe();
    this.subscriptions = new Subscription();
    this.channel?.close();
    this.channel = undefined;
    if (this.heartbeat) clearInterval(this.heartbeat);
  }
}
