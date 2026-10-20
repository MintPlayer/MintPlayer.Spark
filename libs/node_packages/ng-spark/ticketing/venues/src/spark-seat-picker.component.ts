import { ChangeDetectionStrategy, Component, computed, effect, inject, input, model, output, signal } from '@angular/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkLiveService } from '@mintplayer/ng-spark/services';
import { SeatMapPayload, SeatStateFrame } from './generated/contracts';
import { seatMapTopic, SparkSeatMapComponent } from './spark-seat-map.component';
import { SparkVenuesService } from './spark-venues.service';

/**
 * A best-available request from the list alternative (D25): `quantity` places in a block. For a numbered block in a
 * fill chain, `chainKey` names the chain, and checkout claims a `ChainLine` (seats together); for a standing or
 * unnumbered block, checkout claims the block's quota.
 */
export interface SparkBestAvailableRequest {
  blockKey: string;
  chainKey: string | null;
  quantity: number;
}

/** One line of the list alternative. */
export interface SparkSeatPickerOption {
  blockKey: string;
  name: string;
  chainKey: string | null;
  available: number;
  numbered: boolean;
}

/** The list alternative's lines: what is left per block, with the chain a numbered block is filled through. */
export function pickerOptions(payload: SeatMapPayload, frame: SeatStateFrame | null): SparkSeatPickerOption[] {
  const chainOf = new Map<string, string>();
  for (const chain of payload.chains) for (const block of chain.blocks) chainOf.set(block, chain.key);
  const available = new Map((frame?.blocks ?? []).map((b) => [b.key, b.available]));
  return payload.blocks
    // A numbered block outside every chain can only be picked on the map.
    .filter((b) => b.kind !== 'seatedNumbered' || chainOf.has(b.key))
    .map((b) => ({
      blockKey: b.key,
      name: b.name,
      chainKey: b.kind === 'seatedNumbered' ? chainOf.get(b.key) ?? null : null,
      available: available.get(b.key) ?? 0,
      numbered: b.kind === 'seatedNumbered',
    }));
}

/**
 * The buyer's seat picker (PRD §4.11, §4.14): the seat map, and an accessible list alternative (block + quantity =
 * best available) for keyboard and screen-reader buyers, or anyone who prefers it (WCAG 2.1 AA, GA10). The picked
 * seats are listed as text with a remove button each, so the map is never the only way to see or undo a choice.
 *
 * `<spark-seat-picker [snapshotId]="id" [(selected)]="seats" (bestAvailable)="claim($event)" />`
 */
@Component({
  selector: 'spark-seat-picker',
  imports: [TranslateKeyPipe, SparkSeatMapComponent],
  templateUrl: './spark-seat-picker.component.html',
  styleUrl: './spark-seat-picker.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkSeatPickerComponent {
  private readonly venues = inject(SparkVenuesService);
  private readonly live = inject(SparkLiveService);

  snapshotId = input.required<string>();
  selected = model<string[]>([]);
  maxSelection = input(10);
  allowedTags = input(0);
  /** Start on the list (a host that knows the buyer prefers it). */
  startWithList = input(false);

  bestAvailable = output<SparkBestAvailableRequest>();

  protected readonly view = signal<'map' | 'list'>('map');
  protected readonly payload = signal<SeatMapPayload | null>(null);
  private readonly polled = signal<SeatStateFrame | null>(null);
  private readonly liveFrame = this.live.liveTopic<SeatStateFrame>(computed(() => seatMapTopic(this.snapshotId())));
  protected readonly quantities = signal<Record<string, number>>({});

  protected readonly options = computed(() => {
    const payload = this.payload();
    const frame = this.liveFrame() ?? this.polled();
    return payload ? pickerOptions(payload, frame?.snapshotId === payload.snapshotId ? frame : null) : [];
  });

  /** The picked seats as text: block, row, seat. */
  protected readonly pickedSeats = computed(() => {
    const payload = this.payload();
    if (!payload) return [];
    const index = new Map(payload.seats.keys.map((k, i) => [k, i]));
    return this.selected().map((key) => {
      const i = index.get(key);
      if (i === undefined) return { key, label: key };
      const row = payload.seats.row[i];
      const block = payload.blocks[payload.rows.block[row]];
      return { key, label: `${block?.name ?? ''} · ${payload.rows.labels[row]} · ${payload.seats.labels[i]}` };
    });
  });

  constructor() {
    effect(() => this.view.set(this.startWithList() ? 'list' : 'map'));
    effect(() => {
      const id = this.snapshotId();
      void Promise.all([this.venues.seatMap(id), this.venues.seatStates(id)]).then(([payload, frame]) => {
        if (id !== this.snapshotId()) return;
        this.payload.set(payload);
        this.polled.set(frame);
      });
    });
  }

  protected quantity(blockKey: string): number {
    return this.quantities()[blockKey] ?? 1;
  }

  protected setQuantity(blockKey: string, value: string): void {
    const n = Math.max(1, Math.min(this.maxSelection(), Math.floor(Number(value)) || 1));
    this.quantities.update((q) => ({ ...q, [blockKey]: n }));
  }

  protected request(option: SparkSeatPickerOption): void {
    const quantity = this.quantity(option.blockKey);
    if (quantity > option.available) return;
    this.bestAvailable.emit({ blockKey: option.blockKey, chainKey: option.chainKey, quantity });
  }

  protected remove(key: string): void {
    this.selected.set(this.selected().filter((k) => k !== key));
  }
}
