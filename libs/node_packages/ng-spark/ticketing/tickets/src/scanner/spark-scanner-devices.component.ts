import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkDateTimeComponent } from '@mintplayer/ng-spark/date-time';
import { SparkScannerDeviceRegistered } from '../generated/contracts';
import { SparkTicketsService, ticketRefusalKey, ticketRefusalOf } from '../spark-tickets.service';
import { scannerDeviceCredential, scannerDeviceRequest } from './scanner-devices';

/** A check-in list or date the seller may bind the device to. */
export interface SparkScannerDeviceChoice {
  id: string;
  name: string;
}

/**
 * The seller's back-office form that registers a door device (commerce #521 M15, PRD §4.11; the `scanner` role, X8):
 * a name, optionally one check-in list and one date of the event. The device key is shown **once**, to paste into the
 * door scanner page (`<spark-ticket-scanner />`); it is never shown again (the server keeps only its hash). The device
 * just registered can be revoked here; every registered device is listed read-only on the `TicketScannerDevice` page.
 *
 * `<spark-scanner-devices [eventId]="eventId" [lists]="lists" [dates]="dates" />`
 */
@Component({
  selector: 'spark-scanner-devices',
  imports: [TranslateKeyPipe, SparkDateTimeComponent],
  templateUrl: './spark-scanner-devices.component.html',
  styleUrl: './spark-scanner-devices.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkScannerDevicesComponent {
  private readonly tickets = inject(SparkTicketsService);

  /** The event the device scans for. */
  eventId = input.required<string>();

  /** The event's check-in lists; without any, the device uses the server's default list. */
  lists = input<readonly SparkScannerDeviceChoice[]>([]);

  /** The event's dates; without a choice, the device admits for every date of its list. */
  dates = input<readonly SparkScannerDeviceChoice[]>([]);

  protected readonly name = signal('');
  protected readonly listId = signal('');
  protected readonly dateId = signal('');
  protected readonly busy = signal(false);
  protected readonly refusal = signal<string | null>(null);
  protected readonly registered = signal<SparkScannerDeviceRegistered | null>(null);
  protected readonly revoked = signal(false);

  protected credential(device: SparkScannerDeviceRegistered): string {
    return scannerDeviceCredential(device);
  }

  protected canRegister(): boolean {
    return !this.busy() && scannerDeviceRequest(this.eventId(), { name: this.name() }) !== null;
  }

  protected async register(): Promise<void> {
    const request = scannerDeviceRequest(this.eventId(), { name: this.name(), listId: this.listId(), dateId: this.dateId() });
    if (!request) return;
    await this.run(async () => {
      this.revoked.set(false);
      this.registered.set(await this.tickets.registerDevice(request));
      this.name.set('');
    });
  }

  protected async revoke(): Promise<void> {
    const device = this.registered();
    if (!device) return;
    await this.run(async () => {
      await this.tickets.revokeDevice(device.deviceId);
      this.registered.set(null);
      this.revoked.set(true);
    });
  }

  protected async copy(device: SparkScannerDeviceRegistered): Promise<void> {
    try {
      await navigator.clipboard?.writeText(scannerDeviceCredential(device));
    } catch {
      // The key stays on screen to copy by hand.
    }
  }

  private async run(action: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.refusal.set(null);
    try {
      await action();
    } catch (error) {
      const refusal = ticketRefusalOf(error);
      this.refusal.set(ticketRefusalKey(refusal?.code ?? 'forbidden'));
    } finally {
      this.busy.set(false);
    }
  }
}
