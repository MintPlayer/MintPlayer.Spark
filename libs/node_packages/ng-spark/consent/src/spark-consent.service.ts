import { computed, inject, Injectable, signal } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { SPARK_CONFIG } from '@mintplayer/ng-spark';
import { currentLanguage } from '@mintplayer/ng-spark/models';
import { SparkConsentState, SparkConsentUnsubscribeInfo } from './generated/contracts';

/** The purpose names Spark's packages use (`SparkConsentPurposes` on the server). */
export const SparkConsentPurposes = {
  necessary: 'necessary',
  analytics: 'analytics',
  marketingMail: 'marketing-mail',
  thirdPartyChallenge: 'third-party-challenge',
  thirdPartyMedia: 'third-party-media',
} as const;

/** Where a decision was taken (`SparkConsentSources` on the server); stored as part of the proof. */
export type SparkConsentSource = 'banner' | 'settings' | 'checkout' | 'unsubscribe' | 'application';

/**
 * The client of the consent primitive (`/spark/consent`, commerce X21/GA9).
 *
 * A purpose is granted only after an explicit act (GDPR Art. 4(11), Recital 32): an undecided purpose reads as
 * **not granted** everywhere ({@link has}, {@link whenGranted}). Load a third-party script only through
 * {@link whenGranted}, never on page load.
 */
@Injectable({ providedIn: 'root' })
export class SparkConsentService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = (inject(SPARK_CONFIG, { optional: true })?.baseUrl ?? '/spark') + '/consent';

  private readonly stateSignal = signal<SparkConsentState | null>(null);
  private readonly settingsOpenSignal = signal(false);
  private loading: Promise<SparkConsentState | null> | null = null;
  private readonly waiters = new Map<string, Array<() => void>>();

  /** The server's state; `null` until loaded, or when the consent primitive is not registered (404). */
  readonly state = this.stateSignal.asReadonly();

  /** Whether the banner should show: some purpose has no current decision. */
  readonly needsPrompt = computed(() => this.stateSignal()?.needsPrompt ?? false);

  /** Whether the settings dialog was asked for (the banner's "Choose", a footer link). */
  readonly settingsOpen = this.settingsOpenSignal.asReadonly();

  /** Loads the state once (again with `force`). */
  load(force = false): Promise<SparkConsentState | null> {
    if (!force && this.loading) return this.loading;
    this.loading = firstValueFrom(this.http.get<SparkConsentState>(`${this.baseUrl}/`))
      .then(state => this.apply(state))
      .catch((e: unknown) => {
        if (e instanceof HttpErrorResponse && e.status === 404) return this.apply(null);
        this.loading = null;
        throw e;
      });
    return this.loading;
  }

  /** Whether `purpose` is granted now. `necessary` always is; an undecided purpose is not. */
  has(purpose: string): boolean {
    if (purpose === SparkConsentPurposes.necessary) return true;
    return this.stateSignal()?.purposes.find(p => p.name === purpose)?.granted === true;
  }

  /** A signal of {@link has}, for templates and effects. */
  granted(purpose: string) {
    return computed(() => {
      if (purpose === SparkConsentPurposes.necessary) return true;
      return this.stateSignal()?.purposes.find(p => p.name === purpose)?.granted === true;
    });
  }

  /**
   * Resolves once `purpose` is granted: the gate for loading an analytics or third-party script. Never resolves while
   * it is refused or undecided (there is no time-out that would load it anyway).
   */
  whenGranted(purpose: string): Promise<void> {
    if (this.has(purpose)) return Promise.resolve();
    void this.load().catch(() => undefined);
    return new Promise<void>(resolve => {
      const list = this.waiters.get(purpose) ?? [];
      list.push(resolve);
      this.waiters.set(purpose, list);
    });
  }

  /** Grants every declared purpose. */
  acceptAll(source: SparkConsentSource = 'banner'): Promise<SparkConsentState | null> {
    return this.decideAll(true, source);
  }

  /** Refuses every declared purpose: exactly as easy as accepting (GDPR Art. 7(3)). */
  rejectAll(source: SparkConsentSource = 'banner'): Promise<SparkConsentState | null> {
    return this.decideAll(false, source);
  }

  /** Withdraws every consent given (the settings page). */
  withdrawAll(): Promise<SparkConsentState | null> {
    return this.decideAll(false, 'settings');
  }

  /** Records the given decisions; a purpose left out keeps its decision. */
  async save(decisions: Record<string, boolean>, source: SparkConsentSource = 'settings'): Promise<SparkConsentState | null> {
    const state = await firstValueFrom(this.http.post<SparkConsentState>(`${this.baseUrl}/`, {
      decisions,
      source,
      language: currentLanguage(),
    }));
    this.settingsOpenSignal.set(false);
    return this.apply(state);
  }

  openSettings(): void {
    this.settingsOpenSignal.set(true);
  }

  closeSettings(): void {
    this.settingsOpenSignal.set(false);
  }

  /** What an unsubscribe link's page shows; `null` for an invalid link. */
  async describeUnsubscribe(token: string): Promise<SparkConsentUnsubscribeInfo | null> {
    try {
      return await firstValueFrom(this.http.get<SparkConsentUnsubscribeInfo>(`${this.baseUrl}/unsubscribe`, {
        params: new HttpParams().set('token', token),
      }));
    } catch {
      return null;
    }
  }

  /** Withdraws the link's purpose; `false` for an invalid link. */
  async unsubscribe(token: string): Promise<boolean> {
    try {
      await firstValueFrom(this.http.post(`${this.baseUrl}/unsubscribe`, { token }));
      return true;
    } catch {
      return false;
    }
  }

  private async decideAll(granted: boolean, source: SparkConsentSource): Promise<SparkConsentState | null> {
    const state = this.stateSignal() ?? (await this.load());
    if (!state) return null;
    const decisions: Record<string, boolean> = {};
    for (const purpose of state.purposes) {
      if (!purpose.necessary) decisions[purpose.name] = granted;
    }
    return this.save(decisions, source);
  }

  private apply(state: SparkConsentState | null): SparkConsentState | null {
    this.stateSignal.set(state);
    for (const [purpose, list] of [...this.waiters]) {
      if (this.has(purpose)) {
        this.waiters.delete(purpose);
        list.forEach(resolve => resolve());
      }
    }
    return state;
  }
}
