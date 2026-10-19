import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, effect, inject, input, output, signal, untracked, viewChild } from '@angular/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkChallengeOffer, SparkChallengeResponse } from './generated/contracts';
import { SparkChallengeService } from './spark-challenge.service';

/**
 * Shows and solves one challenge offer (commerce #521 M16). ALTCHA runs by itself with a polite status message (no
 * action needed, so nothing to fail for keyboard or screen-reader users); a hosted provider's widget renders inside.
 * Emits `solved` with the response to send back, or `failed` (the host may fetch a new offer).
 *
 * ```html
 * <spark-challenge [offer]="offer" (solved)="join($event)" />
 * ```
 */
@Component({
  selector: 'spark-challenge',
  imports: [TranslateKeyPipe],
  templateUrl: './spark-challenge.component.html',
  styleUrl: './spark-challenge.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkChallengeComponent {
  private readonly challenges = inject(SparkChallengeService);
  private abort?: AbortController;

  /** The server's offer. A new offer starts a new attempt. */
  readonly offer = input<SparkChallengeOffer | null>(null);

  readonly solved = output<SparkChallengeResponse>();
  readonly failed = output<void>();

  protected readonly state = signal<'idle' | 'working' | 'done' | 'failed'>('idle');
  protected readonly widgetHost = viewChild<ElementRef<HTMLElement>>('widget');

  constructor() {
    inject(DestroyRef).onDestroy(() => this.abort?.abort());
    effect(() => {
      const offer = this.offer();
      const host = this.widgetHost()?.nativeElement;
      if (!offer || (offer.provider !== 'altcha' && !host)) return;
      untracked(() => void this.run(offer, host));
    });
  }

  /** Starts again with the same offer (after a failure). */
  retry(): void {
    const offer = this.offer();
    if (offer) void this.run(offer, this.widgetHost()?.nativeElement);
  }

  private async run(offer: SparkChallengeOffer, host: HTMLElement | undefined): Promise<void> {
    this.abort?.abort();
    this.abort = new AbortController();
    this.state.set('working');
    try {
      if (host) host.replaceChildren();
      const response = await this.challenges.solve(offer, host, this.abort.signal);
      this.state.set('done');
      this.solved.emit(response);
    } catch (e) {
      if ((e as DOMException)?.name === 'AbortError') return;
      this.state.set('failed');
      this.failed.emit();
    }
  }
}
