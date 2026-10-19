import { ChangeDetectionStrategy, Component, computed, effect, inject, input, OnInit, signal } from '@angular/core';
import { ResolveTranslationPipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkConsentService, SparkConsentSource } from './spark-consent.service';

/**
 * The consent settings (commerce X21/GA9): one switch per purpose with its description and vendors, plus "Reject all"
 * and "Accept all" with equal weight. Used inside the banner ("Choose") and as the routed settings page, where consent is
 * withdrawn as easily as it was given (GDPR Art. 7(3)). No box is ticked unless the visitor granted that purpose
 * (Recital 32; CJEU C-673/17 Planet49).
 */
@Component({
  selector: 'spark-consent-settings',
  imports: [TranslateKeyPipe, ResolveTranslationPipe],
  templateUrl: './spark-consent-settings.component.html',
  styleUrl: './spark-consent-settings.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkConsentSettingsComponent implements OnInit {
  protected readonly consent = inject(SparkConsentService);

  /** Recorded as the decisions' source. */
  source = input<SparkConsentSource>('settings');

  /** The visitor's switches before saving: only what they granted is on. */
  protected readonly draft = signal<Record<string, boolean>>({});
  protected readonly busy = signal(false);
  protected readonly saved = signal(false);
  protected readonly error = signal(false);

  protected readonly purposes = computed(() => (this.consent.state()?.purposes ?? []).filter(p => !p.necessary));
  protected readonly anyGranted = computed(() => this.purposes().some(p => p.granted === true));

  constructor() {
    effect(() => {
      const draft: Record<string, boolean> = {};
      for (const purpose of this.purposes()) draft[purpose.name] = purpose.granted === true;
      this.draft.set(draft);
    });
  }

  ngOnInit(): void {
    void this.consent.load().catch(() => this.error.set(true));
  }

  protected toggle(name: string, checked: boolean): void {
    this.draft.update(d => ({ ...d, [name]: checked }));
    this.saved.set(false);
  }

  protected save(): Promise<void> {
    return this.run(() => this.consent.save(this.draft(), this.source()));
  }

  protected acceptAll(): Promise<void> {
    return this.run(() => this.consent.acceptAll(this.source()));
  }

  protected rejectAll(): Promise<void> {
    return this.run(() => this.consent.rejectAll(this.source()));
  }

  protected withdrawAll(): Promise<void> {
    return this.run(() => this.consent.withdrawAll());
  }

  private async run(action: () => Promise<unknown>): Promise<void> {
    this.busy.set(true);
    this.error.set(false);
    this.saved.set(false);
    try {
      await action();
      this.saved.set(true);
    } catch {
      this.error.set(true);
    } finally {
      this.busy.set(false);
    }
  }
}
