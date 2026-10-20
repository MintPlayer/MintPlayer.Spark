import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { formatMoney } from '@mintplayer/ng-spark/models';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { ResolveTranslationPipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkAftercareLine, SparkOrderAftercare, SparkRefundLine, SparkWithdrawalResult } from './generated/contracts';
import { refusalKey, refusalOf, SparkCommerceCheckoutService } from './spark-commerce-checkout.service';

/** The withdrawal function's steps (GA1, CRD Art. 11a): button → form → confirmation step → acknowledgement. */
export type SparkWithdrawalStep = 'idle' | 'form' | 'review' | 'done';

/**
 * The order page's after-sale part (commerce #521, M9):
 *
 * - **The withdrawal function** (GA1, Directive 2011/83/EU Art. 11a): a permanently shown, clearly labelled button
 *   ("Withdraw from contract here") while a line still carries a live right; a short form (which lines, an optional
 *   reason, name and e-mail for a guest); a confirmation step that shows what is and is not within the right; then the
 *   acknowledgement with the date and time it was received (also mailed: the durable medium). No approval step.
 * - Cancellation within the seller's policy (D20), with the fee it keeps.
 * - The keep-or-refund choice after a reschedule (D20).
 * - A legal-guarantee claim (GA8).
 * - The refunds and returns so far.
 *
 * `<spark-checkout-aftercare [number]="orderNumber" [access]="signedLinkToken" />`
 */
@Component({
  selector: 'spark-checkout-aftercare',
  imports: [TranslateKeyPipe, ResolveTranslationPipe],
  templateUrl: './spark-checkout-aftercare.component.html',
  styleUrl: './spark-checkout-aftercare.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkCheckoutAftercareComponent {
  private readonly checkout = inject(SparkCommerceCheckoutService);
  private readonly lang = inject(SparkLanguageService);

  /** The order number. */
  number = input.required<string>();

  /** A guest's signed order link token (the `access` query parameter of the mailed link). */
  access = input<string | null>(null);

  protected readonly aftercare = signal<SparkOrderAftercare | null>(null);
  protected readonly step = signal<SparkWithdrawalStep>('idle');
  protected readonly busy = signal(false);
  protected readonly refusal = signal<string | null>(null);
  protected readonly preview = signal<SparkWithdrawalResult | null>(null);
  protected readonly receipt = signal<SparkWithdrawalResult | null>(null);
  protected readonly cancelled = signal<string | null>(null);

  /** Units chosen per line in the withdrawal form. */
  protected readonly chosen = signal<Record<string, number>>({});
  protected readonly note = signal('');
  protected readonly name = signal('');
  protected readonly email = signal('');
  protected readonly claimText = signal('');
  protected readonly claimLine = signal<string | null>(null);

  protected readonly withdrawable = computed(() => (this.aftercare()?.lines ?? []).filter((l) => l.withdrawalLive));
  protected readonly claimable = computed(() => (this.aftercare()?.lines ?? []).filter((l) => l.guaranteeLive));
  protected readonly guest = computed(() => !!this.access());

  constructor() {
    effect(() => {
      const number = this.number();
      const access = this.access();
      this.aftercare.set(null);
      this.step.set('idle');
      void this.load(number, access);
    });
  }

  private async load(number: string, access: string | null): Promise<void> {
    try {
      this.aftercare.set(await this.checkout.aftercare(number, access));
    } catch (e) {
      this.showRefusal(e);
    }
  }

  protected money(value: unknown): string {
    return value ? formatMoney(value) : '';
  }

  protected date(value: string | null | undefined): string {
    return value ? new Date(value).toLocaleString(this.lang.language()) : '';
  }

  protected text(key: string, value: string): string {
    return this.lang.t(key).replace('{0}', value);
  }

  // ---- the withdrawal function (GA1) -----------------------------------------------------------------------------

  protected open(): void {
    const all: Record<string, number> = {};
    for (const line of this.withdrawable()) all[line.lineId] = line.free;
    this.chosen.set(all);
    this.refusal.set(null);
    this.step.set('form');
  }

  protected setUnits(line: SparkAftercareLine, value: string): void {
    const units = Math.max(0, Math.min(line.free, Number.parseInt(value, 10) || 0));
    this.chosen.update((c) => ({ ...c, [line.lineId]: units }));
  }

  private lines(): SparkRefundLine[] {
    return Object.entries(this.chosen()).filter(([, q]) => q > 0).map(([lineId, quantity]) => ({ lineId, quantity }));
  }

  /** The confirmation step: what is and is not within the right, before anything is recorded. */
  protected async review(): Promise<void> {
    await this.run(async () => {
      this.preview.set(await this.checkout.withdraw(this.request(false)));
      this.step.set('review');
    });
  }

  protected async confirm(): Promise<void> {
    await this.run(async () => {
      this.receipt.set(await this.checkout.withdraw(this.request(true)));
      this.step.set('done');
      await this.load(this.number(), this.access());
    });
  }

  private request(confirm: boolean) {
    return {
      number: this.number(),
      access: this.access(),
      lines: this.lines(),
      note: this.note() || null,
      name: this.name() || null,
      email: this.email() || null,
      confirm,
    };
  }

  // ---- cancellation (D20), reschedule (D20), guarantee (GA8) ------------------------------------------------------

  protected async cancel(line: SparkAftercareLine): Promise<void> {
    await this.run(async () => {
      const result = await this.checkout.cancel({ number: this.number(), access: this.access(), lines: [{ lineId: line.lineId, quantity: line.free }], confirm: true });
      this.cancelled.set(formatMoney(result.amount));
      await this.load(this.number(), this.access());
    });
  }

  protected async choose(windowId: string, choice: 'keep' | 'refund'): Promise<void> {
    await this.run(async () => {
      this.aftercare.set(await this.checkout.chooseReschedule({ number: this.number(), access: this.access(), windowId, choice }));
    });
  }

  protected async claim(): Promise<void> {
    const lineId = this.claimLine();
    const description = this.claimText().trim();
    if (!lineId || !description) return;
    await this.run(async () => {
      this.aftercare.set(await this.checkout.claim({ number: this.number(), access: this.access(), lines: [{ lineId, quantity: 1 }], description }));
      this.claimText.set('');
    });
  }

  private async run(action: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.refusal.set(null);
    try {
      await action();
    } catch (e) {
      this.showRefusal(e);
    } finally {
      this.busy.set(false);
    }
  }

  private showRefusal(error: unknown): void {
    const refusal = refusalOf(error);
    this.refusal.set(refusalKey(refusal?.code ?? 'conflict', this.lang));
  }
}
