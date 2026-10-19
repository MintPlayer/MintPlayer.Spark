import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SparkConsentUnsubscribeInfo } from './generated/contracts';
import { SparkConsentService } from './spark-consent.service';

/**
 * The page behind a marketing mail's unsubscribe link (commerce §4.20, GA9): one button, no sign-in. Opening the page
 * changes nothing (mail scanners prefetch links); the button withdraws the consent (ePrivacy Art. 13(2), GDPR
 * Art. 21(3)). Needs `withComponentInputBinding()` for the `token` query parameter.
 */
@Component({
  selector: 'spark-consent-unsubscribe',
  imports: [TranslateKeyPipe],
  templateUrl: './spark-consent-unsubscribe.component.html',
  styleUrl: './spark-consent-unsubscribe.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkConsentUnsubscribeComponent {
  private readonly consent = inject(SparkConsentService);
  private readonly lang = inject(SparkLanguageService);

  /** The link's token (`?token=`). */
  token = input<string | null>(null);

  protected readonly info = signal<SparkConsentUnsubscribeInfo | null>(null);
  protected readonly status = signal<'loading' | 'ready' | 'done' | 'invalid'>('loading');
  protected readonly busy = signal(false);

  constructor() {
    effect(() => {
      const token = this.token();
      if (!token) {
        this.status.set('invalid');
        return;
      }
      this.status.set('loading');
      void this.consent.describeUnsubscribe(token).then(info => {
        this.info.set(info);
        this.status.set(info ? (info.granted ? 'ready' : 'done') : 'invalid');
      });
    });
  }

  protected question(): string {
    return this.lang.t('consent.unsubscribe.text').replace('{0}', this.info()?.recipient ?? '');
  }

  protected async unsubscribe(): Promise<void> {
    const token = this.token();
    if (!token) return;
    this.busy.set(true);
    const ok = await this.consent.unsubscribe(token);
    this.busy.set(false);
    this.status.set(ok ? 'done' : 'invalid');
  }
}
