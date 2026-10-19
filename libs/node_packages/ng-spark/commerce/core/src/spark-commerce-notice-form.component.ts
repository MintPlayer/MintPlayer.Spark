import { ChangeDetectionStrategy, Component, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { CommerceNoticeReceipt } from './generated/contracts';
import { SparkCommerceService } from './spark-commerce.service';

/** The notice reason categories (`DsaReasons` on the server). */
export const SPARK_COMMERCE_DSA_REASONS = [
  'illegal-speech', 'consumer-information', 'intellectual-property', 'unsafe-products', 'scams-and-fraud',
  'data-protection', 'protection-of-minors', 'terms-violation', 'other',
] as const;

/**
 * The DSA notice form (commerce #521 M4, GA5; Regulation (EU) 2022/2065 Art. 16): anyone, signed in or
 * not, reports a listing, an event or a review — reason, a substantiated explanation, the exact
 * location, optional name and e-mail, and the required good-faith statement. On success it shows the
 * confirmation of receipt (Art. 16(4)) and emits the receipt.
 */
@Component({
  selector: 'spark-commerce-notice-form',
  imports: [FormsModule, TranslateKeyPipe],
  templateUrl: './spark-commerce-notice-form.component.html',
  styleUrl: './spark-commerce-notice-form.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkCommerceNoticeFormComponent {
  private readonly commerce = inject(SparkCommerceService);

  /** `listing`, `event` or `review`. */
  targetKind = input.required<string>();
  /** The reported document's id. */
  targetId = input.required<string>();

  /** Emitted with the receipt once the notice was accepted. */
  submitted = output<CommerceNoticeReceipt>();

  protected readonly reasons = SPARK_COMMERCE_DSA_REASONS;
  protected reason = '';
  protected explanation = '';
  protected location = typeof location !== 'undefined' ? location.href : '';
  protected contactName = '';
  protected contactEmail = '';
  protected goodFaith = false;

  protected readonly busy = signal(false);
  protected readonly receipt = signal<CommerceNoticeReceipt | null>(null);
  protected readonly error = signal<string | null>(null);

  protected get canSend(): boolean {
    return !!this.reason && this.explanation.trim().length > 0 && this.goodFaith && !this.busy();
  }

  protected async send(): Promise<void> {
    if (!this.canSend) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const receipt = await this.commerce.submitNotice({
        targetKind: this.targetKind(),
        targetId: this.targetId(),
        reason: this.reason,
        explanation: this.explanation,
        location: this.location || null,
        contactName: this.contactName || null,
        contactEmail: this.contactEmail || null,
        goodFaith: this.goodFaith,
      });
      this.receipt.set(receipt);
      this.submitted.emit(receipt);
    } catch (e) {
      this.error.set((e as { message?: string })?.message ?? 'error');
    } finally {
      this.busy.set(false);
    }
  }
}
