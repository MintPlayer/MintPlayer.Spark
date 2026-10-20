import { ChangeDetectionStrategy, Component, computed, DestroyRef, effect, inject, input, output, signal, untracked } from '@angular/core';
import { currentLanguage } from '@mintplayer/ng-spark/models';
import { formatSparkDateTime } from '@mintplayer/ng-spark/date-time';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkBasket } from './generated/contracts';
import { crossedMark, holdTimerState } from './hold-timer';
import { refusalKey, refusalOf, SparkCommerceCheckoutService } from './spark-commerce-checkout.service';

/**
 * The hold timer (GA10, PRD §4.14; WCAG 2.2 SC 2.2.1 "Extend"): the deadline as text, a visual countdown that is not
 * read out every second, polite announcements at 5 min, 1 min and 20 s (see `hold-timer.ts`), and a single-action
 * **"More time"** button from 5 minutes before the deadline for as long as the server reports a step left
 * (`extendableUntil`, capped at the channel's `Hold:Max`). Beyond that cap the hold relies on the real-time exception of
 * SC 2.2.1, explained in the accessibility statement. The `PaymentPending` window is shown the same way, without the
 * button (the provider's limit, not ours: pass no `extendableUntil`).
 *
 * `<spark-checkout-hold-timer [deadline]="basket.earliestHoldUntil" [extendableUntil]="basket.extendableUntil" (extended)="basket = $event" />`
 */
@Component({
  selector: 'spark-checkout-hold-timer',
  imports: [TranslateKeyPipe],
  templateUrl: './spark-checkout-hold-timer.component.html',
  styleUrl: './spark-checkout-hold-timer.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkCheckoutHoldTimerComponent {
  private readonly checkout = inject(SparkCommerceCheckoutService);
  private readonly lang = inject(SparkLanguageService);

  /** The ISO instant the hold lapses; nothing renders without one. */
  deadline = input<string | null | undefined>(null);

  /** The server's deadline after one "More time" (`SparkBasket.extendableUntil`); null hides the button. */
  extendableUntil = input<string | null | undefined>(null);

  /** The text key of the deadline line; `{0}` is the time. The payment window passes its own. */
  deadlineKey = input('commerceCheckout.holdTimer.until');

  /** "More time" succeeded: the basket with the new deadlines. */
  readonly extended = output<SparkBasket>();

  private readonly now = signal(Date.now());
  protected readonly busy = signal(false);
  protected readonly refusal = signal<string | null>(null);
  /** The polite live region's text: set only when a mark is crossed or after "More time". */
  protected readonly announcement = signal('');
  private previousLeft: number | null = null;

  protected readonly state = computed(() => holdTimerState(this.deadline(), this.extendableUntil(), this.now()));

  protected readonly deadlineText = computed(() => {
    const deadline = this.deadline();
    return deadline
      ? this.lang.t(this.deadlineKey()).replace('{0}', formatSparkDateTime(deadline, 'viewer', 'time', currentLanguage()))
      : '';
  });

  protected readonly extendText = computed(() => {
    const until = this.extendableUntil();
    return until ? this.lang.t('commerceCheckout.holdTimer.moreTimeUntil').replace('{0}', formatSparkDateTime(until, 'viewer', 'time', currentLanguage())) : '';
  });

  constructor() {
    const timer = setInterval(() => this.now.set(Date.now()), 1000);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));

    effect(() => {
      const left = this.state().secondsLeft;
      untracked(() => {
        const mark = crossedMark(this.previousLeft, left);
        this.previousLeft = left;
        if (mark !== null) this.announcement.set(this.markText(mark));
      });
    });
  }

  protected async moreTime(): Promise<void> {
    if (this.busy()) return;
    this.busy.set(true);
    this.refusal.set(null);
    try {
      const basket = await this.checkout.extendHold();
      this.previousLeft = null;
      this.announcement.set(basket.earliestHoldUntil
        ? this.lang.t('commerceCheckout.holdTimer.extended').replace('{0}', formatSparkDateTime(basket.earliestHoldUntil, 'viewer', 'time', currentLanguage()))
        : '');
      this.extended.emit(basket);
    } catch (e) {
      this.refusal.set(refusalKey(refusalOf(e)?.code ?? 'conflict', this.lang));
    } finally {
      this.busy.set(false);
    }
  }

  private markText(mark: number): string {
    if (mark === 60) return this.lang.t('commerceCheckout.holdTimer.oneMinuteLeft');
    if (mark > 60) return this.lang.t('commerceCheckout.holdTimer.minutesLeft').replace('{0}', String(Math.round(mark / 60)));
    return this.lang.t('commerceCheckout.holdTimer.secondsLeft').replace('{0}', String(mark));
  }
}
