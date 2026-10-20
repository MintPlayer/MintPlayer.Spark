import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, inject, signal, viewChild } from '@angular/core';
import { Subscription } from 'rxjs';
import { ResolveTranslationPipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkLiveService } from '@mintplayer/ng-spark/services';
import { openSparkOfflineStore, requestPersistentStorage, SparkOfflineStore } from '@mintplayer/ng-spark/offline';
import { SparkBarcodeCamera, sparkCameraScanningAvailable } from '@mintplayer/ng-spark/scanning';
import { SparkScannerNudge, SparkScannerSession } from '../generated/contracts';
import { TicketScanDirection } from '../check-in-rules';
import { SparkTicketScannerClient } from './scanner-client';
import { TicketScannerEngine, TicketScanOutcome } from './ticket-scanner';

const DEVICE_PARTITION = 'scanner-device';
const CREDENTIAL = 'credential';
const SYNC_INTERVAL_MS = 30_000;

/**
 * The door scanner PWA page (commerce #521 M15, PRD §4.11): paste the device key once (registered in the back office
 * for one event and check-in list, the `scanner` role), then scan QR codes with the camera (`ng-spark/scanning`) or
 * type a code. Online scans are atomic on the server; offline ones are checked against the downloaded PBKDF2 list and
 * queued (`ng-spark/offline`), then synced every 30 s, on reconnect, and when the event's tickets change (the
 * `ticketing-scanner:{eventId}` live topic). Results show the "ID check" banner (GB12) and "on hold" (GB1); double
 * entries found on sync are listed. A revoked device, or one past the event, wipes its data.
 *
 * `<spark-ticket-scanner />` on the route `/scanner` of an installed PWA (`ng-spark/pwa`).
 */
@Component({
  selector: 'spark-ticket-scanner',
  imports: [TranslateKeyPipe, ResolveTranslationPipe],
  templateUrl: './spark-ticket-scanner.component.html',
  styleUrl: './spark-ticket-scanner.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkTicketScannerComponent {
  private readonly client = inject(SparkTicketScannerClient);
  private readonly live = inject(SparkLiveService);
  private readonly video = viewChild<ElementRef<HTMLVideoElement>>('video');

  protected readonly credentialInput = signal('');
  protected readonly session = signal<SparkScannerSession | null>(null);
  protected readonly direction = signal<TicketScanDirection>('In');
  protected readonly outcome = signal<TicketScanOutcome | null>(null);
  protected readonly manualCode = signal('');
  protected readonly listed = signal(0);
  protected readonly queued = signal(0);
  protected readonly doubleEntries = signal<string[]>([]);
  protected readonly online = signal(typeof navigator === 'undefined' ? true : navigator.onLine);
  protected readonly wiped = signal(false);
  protected readonly cameraOn = signal(false);
  protected readonly cameraAvailable = sparkCameraScanningAvailable();
  protected readonly persistent = signal(true);
  protected readonly busy = signal(false);
  protected readonly frozen = signal(false);

  private engine: TicketScannerEngine | null = null;
  private deviceStore: SparkOfflineStore<string> | null = null;
  private readonly camera = new SparkBarcodeCamera(value => void this.scan(value));
  private timer: ReturnType<typeof setInterval> | null = null;
  private topic: Subscription | null = null;

  constructor() {
    const onOnline = () => {
      this.online.set(true);
      void this.sync();
    };
    const onOffline = () => this.online.set(false);
    if (typeof window !== 'undefined') {
      window.addEventListener('online', onOnline);
      window.addEventListener('offline', onOffline);
    }
    inject(DestroyRef).onDestroy(() => {
      if (typeof window !== 'undefined') {
        window.removeEventListener('online', onOnline);
        window.removeEventListener('offline', onOffline);
      }
      if (this.timer) clearInterval(this.timer);
      this.topic?.unsubscribe();
      void this.camera.stop();
    });
    void this.resume();
  }

  /** A device that was set up before starts again with its stored credential (it may be offline). */
  private async resume(): Promise<void> {
    this.deviceStore = await openSparkOfflineStore<string>(DEVICE_PARTITION);
    const credential = await this.deviceStore.get(CREDENTIAL);
    if (credential) await this.begin(credential);
  }

  protected async setUp(): Promise<void> {
    const credential = this.credentialInput().trim();
    if (!/^[0-9a-f]{32}\.[A-Za-z0-9_-]{20,}$/.test(credential)) return;
    await this.deviceStore?.put(CREDENTIAL, credential);
    this.credentialInput.set('');
    await this.begin(credential);
  }

  private async begin(credential: string): Promise<void> {
    this.busy.set(true);
    try {
      const key = credential.slice(0, credential.indexOf('.'));
      this.engine = new TicketScannerEngine(this.client.withCredential(credential), {
        list: await openSparkOfflineStore(`scanner-list:${key}`),
        queue: await openSparkOfflineStore(`scanner-queue:${key}`),
        meta: await openSparkOfflineStore(`scanner-meta:${key}`),
      });
      const session = await this.engine.start();
      if (!session) {
        await this.forget();
        return;
      }
      this.session.set(session);
      this.persistent.set(await requestPersistentStorage());
      if ((await this.engine.status()).listed === 0 && this.online()) await this.engine.download().catch(() => undefined);
      await this.refresh();
      this.timer = setInterval(() => void this.sync(), SYNC_INTERVAL_MS);
      this.topic = this.live.watchTopic<SparkScannerNudge>(`ticketing-scanner:${session.eventId}`).subscribe(event => {
        if (event.type === 'topic') void this.sync();
      });
    } finally {
      this.busy.set(false);
    }
  }

  /** The device was revoked or the event is over: everything it held is gone. */
  private async forget(): Promise<void> {
    await this.deviceStore?.delete(CREDENTIAL);
    this.session.set(null);
    this.engine = null;
    this.wiped.set(true);
    if (this.timer) clearInterval(this.timer);
    this.topic?.unsubscribe();
    await this.camera.stop();
    this.cameraOn.set(false);
  }

  private async refresh(): Promise<void> {
    if (!this.engine) return;
    const wipeAfter = this.session()?.wipeAfter;
    if (wipeAfter && Date.now() >= Date.parse(wipeAfter)) {
      // The event is over (§4.11): the list and any scans still queued go.
      await this.engine.wipe();
      await this.forget();
      return;
    }
    const status = await this.engine.status();
    this.listed.set(status.listed);
    this.queued.set(status.queued);
    this.doubleEntries.set(status.doubleEntries);
    const freezeAt = this.session()?.freezeAt;
    this.frozen.set(!!freezeAt && Date.now() >= Date.parse(freezeAt));
  }

  protected async sync(): Promise<void> {
    if (!this.engine || !this.online()) return;
    try {
      await this.engine.sync();
    } catch {
      // Still offline, or the server is busy: the queue stays for the next attempt.
    }
    if (!this.engine.current) {
      await this.forget();
      return;
    }
    await this.refresh();
  }

  protected async scan(payload: string): Promise<void> {
    if (!this.engine) return;
    try {
      this.outcome.set(await this.engine.scan(payload, this.direction()));
    } catch {
      this.outcome.set({ result: 'unknown', offline: !this.online(), doubleEntry: false });
    }
    if (!this.engine?.current) {
      await this.forget();
      return;
    }
    navigator.vibrate?.(this.outcome()?.result === 'admitted' || this.outcome()?.result === 'exited' ? 60 : [80, 60, 80]);
    await this.refresh();
  }

  protected async scanManual(): Promise<void> {
    const value = this.manualCode().trim();
    if (!value) return;
    this.manualCode.set('');
    await this.scan(value);
  }

  protected async toggleCamera(): Promise<void> {
    const video = this.video()?.nativeElement;
    if (this.camera.running) {
      await this.camera.stop();
      this.cameraOn.set(false);
    } else if (video) {
      this.cameraOn.set(await this.camera.start(video));
    }
  }
}
