import { ChangeDetectionStrategy, Component, inject, input, OnInit, signal } from '@angular/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkConsentService } from './spark-consent.service';
import { SparkConsentSettingsComponent } from './spark-consent-settings.component';

/**
 * The consent banner (commerce X21/GA9). Shown while a purpose has no current decision; place it once, in the app
 * shell. Replaceable: an app may build its own on {@link SparkConsentService}.
 *
 * Built against the rules the EDPB cookie-banner taskforce (report of 17 January 2023) and GDPR Art. 7 set:
 * "Accept all" and "Reject all" are on the first layer, side by side, with the **same** style, size and weight (no
 * dark pattern); nothing is pre-ticked; closing the banner without choosing records nothing (the banner returns);
 * the page stays usable behind it (a non-modal region, not a dialog that traps focus). ⚠️ Whether a "Reject all" on the
 * first layer is mandatory differs per national authority (CNIL: yes); this component always shows it.
 */
@Component({
  selector: 'spark-consent-banner',
  imports: [TranslateKeyPipe, SparkConsentSettingsComponent],
  templateUrl: './spark-consent-banner.component.html',
  styleUrl: './spark-consent-banner.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkConsentBannerComponent implements OnInit {
  protected readonly consent = inject(SparkConsentService);

  /** Overrides the policy link of the server's state. */
  policyUrl = input<string | null>(null);

  protected readonly busy = signal(false);
  protected readonly error = signal(false);

  ngOnInit(): void {
    void this.consent.load().catch(() => this.error.set(true));
  }

  protected async run(action: () => Promise<unknown>): Promise<void> {
    this.busy.set(true);
    this.error.set(false);
    try {
      await action();
    } catch {
      this.error.set(true);
    } finally {
      this.busy.set(false);
    }
  }

  protected acceptAll(): Promise<void> {
    return this.run(() => this.consent.acceptAll('banner'));
  }

  protected rejectAll(): Promise<void> {
    return this.run(() => this.consent.rejectAll('banner'));
  }
}
