import { afterNextRender, ChangeDetectionStrategy, Component, DestroyRef, effect, ElementRef, inject, input, signal, untracked, viewChild } from '@angular/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { LayoutDiffResult, VenueLayoutPlanResponse } from './generated/contracts';
import { packPlan } from './layout-editor/plan-pack';
import { SeatMapController } from './seat-map/seat-map-controller';
import { buildSeatMapModel, SeatMapModel } from './seat-map/seat-map-model';
import { readSeatMapColors } from './seat-map/seat-map-theme';
import { SparkVenuesService } from './spark-venues.service';

/** The highlight marks of a diff over a model: added, moved and changed seats. */
export function diffMarks(model: SeatMapModel, diff: LayoutDiffResult): Uint8Array {
  const marks = new Uint8Array(model.count);
  for (const key of [...diff.added, ...diff.moved, ...diff.changed]) {
    const i = model.keyIndex.get(key);
    if (i !== undefined) marks[i] = 1;
  }
  return marks;
}

/**
 * The approver's view of a layout publication (§4.13: "seat layouts get a custom visual diff"): the proposed plan with
 * every added, moved or retagged seat outlined, and the counts, including the seats removed. The decision itself is
 * Approvals' (`<spark-approval-request>`); this is what the approver looks at.
 *
 * `<spark-venue-layout-review [layoutId]="request.subject.id" />`
 */
@Component({
  selector: 'spark-venue-layout-review',
  imports: [TranslateKeyPipe],
  templateUrl: './spark-venue-layout-review.component.html',
  styleUrl: './spark-venue-layout-review.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkVenueLayoutReviewComponent {
  private readonly venues = inject(SparkVenuesService);
  private readonly host = inject(ElementRef<HTMLElement>);

  layoutId = input.required<string>();

  protected readonly canvas = viewChild.required<ElementRef<HTMLCanvasElement>>('canvas');
  protected readonly diff = signal<LayoutDiffResult | null>(null);
  protected readonly plan = signal<VenueLayoutPlanResponse | null>(null);
  protected readonly failed = signal(false);

  private controller: SeatMapController | null = null;
  private model: SeatMapModel | null = null;
  private marks: Uint8Array | null = null;

  constructor() {
    afterNextRender(() => {
      this.controller = new SeatMapController(this.canvas().nativeElement, {
        options: () => ({ highlight: this.marks, revision: this.marks ? 1 : 0, lod: this.model && this.controller && this.model.pitch * this.controller.currentViewport.scale < 3 ? 'dots' : null }),
      });
      this.controller.colors = readSeatMapColors(this.host.nativeElement);
      if (this.model) this.controller.setModel(this.model);
    });
    inject(DestroyRef).onDestroy(() => this.controller?.destroy());
    effect(() => {
      const id = this.layoutId();
      untracked(() => void this.load(id));
    });
  }

  private async load(layoutId: string): Promise<void> {
    const [proposal, diff] = await Promise.all([
      this.venues.plan(layoutId, 'proposal').then((p) => p ?? this.venues.plan(layoutId, 'draft')),
      this.venues.diff(layoutId),
    ]);
    if (!proposal || !diff) { this.failed.set(true); return; }
    this.plan.set(proposal);
    this.diff.set(diff);
    this.model = buildSeatMapModel(packPlan(proposal.plan));
    this.marks = diffMarks(this.model, diff);
    this.controller?.setModel(this.model);
  }
}
