import {
  afterNextRender, ChangeDetectionStrategy, Component, computed, DestroyRef, effect, ElementRef, inject, input, model, signal, viewChild,
} from '@angular/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkLiveService } from '@mintplayer/ng-spark/services';
import { SeatMapPayload, SeatStateFrame } from './generated/contracts';
import { SeatMapController } from './seat-map/seat-map-controller';
import { applyStateFrame, buildSeatMapModel, hitTest, SEAT_FREE, SeatMapModel, TAG_ACCESSIBLE, TAG_COMPANION } from './seat-map/seat-map-model';
import { readSeatMapColors } from './seat-map/seat-map-theme';
import { SparkVenuesService } from './spark-venues.service';

/** The live topic of a snapshot's seat states (the server's `VenueSeatMaps.Topic`). */
export function seatMapTopic(snapshotId: string): string {
  return `ticketing-venues:${snapshotId}`;
}

/**
 * Whether a buyer may pick seat `index` (default 3): a wheelchair space only with a ticket type that allows
 * {@link TAG_ACCESSIBLE}; a companion seat with one that allows {@link TAG_COMPANION}, or together with the wheelchair
 * space beside it. The server checks the same rule before it claims (`AccessibleSeating.Check`).
 */
export function seatPickable(model: SeatMapModel, index: number, allowedTags: number, selected: ReadonlySet<number>): boolean {
  const tags = model.tags[index];
  if (tags & TAG_ACCESSIBLE) return (allowedTags & TAG_ACCESSIBLE) !== 0;
  if (tags & TAG_COMPANION) {
    if (allowedTags & TAG_COMPANION) return true;
    for (const neighbour of [index - 1, index + 1]) {
      if (neighbour >= 0 && neighbour < model.count && model.payload.seats.row[neighbour] === model.payload.seats.row[index]
        && (model.tags[neighbour] & TAG_ACCESSIBLE) && selected.has(neighbour)) return true;
    }
    return false;
  }
  return true;
}

/**
 * The buyer's seat map of one snapshot (S7, PRD §4.11): canvas with level of detail, live seat states from the topic
 * `ticketing-venues:{snapshotId}` (X2), pan and zoom by mouse, touch (pinch) and keyboard, and pick your seat (D7).
 * `selected` holds seat keys (two-way). Screen-reader and keyboard buyers use the list alternative of
 * `<spark-seat-picker>`, which wraps this map.
 *
 * `<spark-seat-map [snapshotId]="date.snapshotId" [(selected)]="seats" [maxSelection]="6" />`
 */
@Component({
  selector: 'spark-seat-map',
  imports: [TranslateKeyPipe],
  templateUrl: './spark-seat-map.component.html',
  styleUrl: './spark-seat-map.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkSeatMapComponent {
  private readonly venues = inject(SparkVenuesService);
  private readonly live = inject(SparkLiveService);
  private readonly host = inject(ElementRef<HTMLElement>);

  snapshotId = input.required<string>();
  /** Seat keys the buyer picked. */
  selected = model<string[]>([]);
  /** How many seats one buyer may pick (the sale's per-order limit). */
  maxSelection = input(10);
  /** The restricting seat tags the chosen ticket type allows (accessible, companion). */
  allowedTags = input(0);
  /** Follow the live topic (off for a static preview). */
  liveStates = input(true);
  /** Picking off: the map only shows availability. */
  readOnly = input(false);

  protected readonly canvas = viewChild.required<ElementRef<HTMLCanvasElement>>('canvas');
  protected readonly payload = signal<SeatMapPayload | null>(null);
  protected readonly failed = signal(false);
  protected readonly announcement = signal('');

  private controller: SeatMapController | null = null;
  private modelRef: SeatMapModel | null = null;
  private revision = 0;
  private selectedMarks: Uint8Array | null = null;

  private readonly topic = computed(() => (this.liveStates() ? seatMapTopic(this.snapshotId()) : null));
  private readonly frame = this.live.liveTopic<SeatStateFrame>(this.topic);

  constructor() {
    afterNextRender(() => {
      this.controller = new SeatMapController(this.canvas().nativeElement, {
        options: () => ({ selected: this.selectedMarks, revision: this.revision }),
        onTap: (x, y) => this.tap(x, y),
      });
      this.controller.colors = readSeatMapColors(this.host.nativeElement);
      if (this.modelRef) this.controller.setModel(this.modelRef);
    });
    inject(DestroyRef).onDestroy(() => this.controller?.destroy());

    effect(() => {
      const id = this.snapshotId();
      this.payload.set(null);
      this.failed.set(false);
      void this.load(id);
    });
    effect(() => {
      const frame = this.frame();
      if (frame && this.modelRef && frame.snapshotId === this.snapshotId() && applyStateFrame(this.modelRef, frame)) this.controller?.invalidate();
    });
    effect(() => {
      const keys = this.selected();
      if (!this.modelRef) return;
      this.selectedMarks = new Uint8Array(this.modelRef.count);
      for (const key of keys) {
        const i = this.modelRef.keyIndex.get(key);
        if (i !== undefined) this.selectedMarks[i] = 1;
      }
      this.revision++;
      this.controller?.requestRender();
    });
  }

  zoomIn(): void { this.controller?.zoomBy(1.4); }

  zoomOut(): void { this.controller?.zoomBy(1 / 1.4); }

  fit(): void { this.controller?.fit(); }

  private async load(snapshotId: string): Promise<void> {
    try {
      const [payload, state] = await Promise.all([this.venues.seatMap(snapshotId), this.venues.seatStates(snapshotId)]);
      if (snapshotId !== this.snapshotId()) return;
      if (!payload) {
        this.failed.set(true);
        return;
      }
      const built = buildSeatMapModel(payload);
      if (state) applyStateFrame(built, state);
      this.modelRef = built;
      this.payload.set(payload);
      this.selected.set([...this.selected()]); // re-mark against the new model
      this.controller?.setModel(built);
    } catch {
      this.failed.set(true);
    }
  }

  private tap(x: number, y: number): void {
    const m = this.modelRef;
    if (!m || this.readOnly()) return;
    const index = hitTest(m, x, y, m.pitch * 0.6);
    if (index < 0) return;
    const key = m.payload.seats.keys[index];
    const current = this.selected();
    const label = this.describe(m, index);
    if (current.includes(key)) {
      this.selected.set(current.filter((k) => k !== key));
      this.announcement.set(label);
      return;
    }
    const selectedIndexes = new Set(current.map((k) => m.keyIndex.get(k)).filter((i): i is number => i !== undefined));
    if (m.state[index] !== SEAT_FREE || !seatPickable(m, index, this.allowedTags(), selectedIndexes) || current.length >= this.maxSelection()) return;
    this.selected.set([...current, key]);
    this.announcement.set(label);
  }

  private describe(m: SeatMapModel, index: number): string {
    const row = m.payload.seats.row[index];
    const block = m.payload.blocks[m.block[index]];
    return `${block?.name ?? ''} ${m.payload.rows.labels[row] ?? ''} ${m.payload.seats.labels[index]}`.trim();
  }
}
