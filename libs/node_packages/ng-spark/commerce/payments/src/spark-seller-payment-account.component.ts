import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkSellerPaymentStatus } from './generated/contracts';
import { SparkCommercePaymentsService } from './spark-commerce-payments.service';

/** Where the browser goes; a seam so tests do not navigate. */
export type SparkPaymentsNavigate = (url: string) => void;

/**
 * A seller's payment account (D5, D21; commerce #521 M6): its verification state at the provider, what the provider
 * still needs, and the button that starts (or resumes) Connect onboarding. Renders nothing for a caller who may not
 * see the seller's account (the server's refusal).
 *
 * `<spark-seller-payment-account [organizationId]="org.id" />`
 */
@Component({
  selector: 'spark-seller-payment-account',
  imports: [TranslateKeyPipe],
  templateUrl: './spark-seller-payment-account.component.html',
  styleUrl: './spark-seller-payment-account.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkSellerPaymentAccountComponent {
  private readonly payments = inject(SparkCommercePaymentsService);

  /** The seller organization. */
  organizationId = input.required<string>();

  /** The provider to link; the server's default when empty. */
  provider = input<string>('');

  /** How to leave for the provider's onboarding; `location.assign` by default. */
  navigate = input<SparkPaymentsNavigate>((url) => window.location.assign(url));

  protected readonly status = signal<SparkSellerPaymentStatus | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal(false);

  constructor() {
    effect(() => {
      const organizationId = this.organizationId();
      this.status.set(null);
      void this.payments.status(organizationId).then((status) => this.status.set(status), () => this.error.set(true));
    });
  }

  /** The translation key of the state's label. */
  protected stateKey(status: SparkSellerPaymentStatus): string {
    switch (status.kyc) {
      case 'verified': return 'commercePayments.connectVerified';
      case 'pending': return 'commercePayments.connectPending';
      case 'rejected': return 'commercePayments.connectRejected';
      default: return 'commercePayments.connectNotStarted';
    }
  }

  protected async link(): Promise<void> {
    this.busy.set(true);
    this.error.set(false);
    try {
      const url = await this.payments.startOnboarding(this.organizationId(), this.provider() || undefined);
      this.navigate()(url);
    } catch {
      this.error.set(true);
    } finally {
      this.busy.set(false);
    }
  }
}
