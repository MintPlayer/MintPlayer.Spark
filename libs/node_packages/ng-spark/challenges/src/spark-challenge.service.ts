import { inject, Injectable } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { SPARK_CONFIG } from '@mintplayer/ng-spark';
import { SparkConsentService } from '@mintplayer/ng-spark/consent';
import { solveAltcha } from './altcha-solver';
import { SparkChallengeOffer, SparkChallengeResponse } from './generated/contracts';

/** The challenge points (`SparkChallengePoints` on the server). `admission` is never challenged. */
export const SparkChallengePoints = {
  register: 'register',
  queueJoin: 'queue-join',
  hold: 'hold',
  checkout: 'checkout',
  form: 'form',
} as const;

/** The consent purpose hosted providers need (GA9). */
const THIRD_PARTY_CHALLENGE = 'third-party-challenge';

/** A hosted provider's global API, as its documented script installs it. */
interface WidgetApi {
  render(element: HTMLElement, options: Record<string, unknown>): unknown;
}

/**
 * The client of `MintPlayer.Spark.Challenges` (commerce #521 M16): fetches an offer, and solves it.
 *
 * - **ALTCHA** (built in, no third party) is solved with WebCrypto, without any action from the visitor.
 * - **Hosted providers** (Turnstile, hCaptcha, reCAPTCHA, Friendly Captcha) render their widget into an element; their
 *   script is loaded only when the visitor has granted `third-party-challenge` (GA9). Without consent the server
 *   already offers ALTCHA instead (`consentFallback`), so a refusal never blocks anyone.
 */
@Injectable({ providedIn: 'root' })
export class SparkChallengeService {
  private readonly http = inject(HttpClient);
  private readonly consent = inject(SparkConsentService, { optional: true });
  private readonly baseUrl = (inject(SPARK_CONFIG, { optional: true })?.baseUrl ?? '/spark') + '/challenges';
  private readonly scripts = new Map<string, Promise<void>>();

  /** The challenge to show at a point; `null` when no provider is configured (404). */
  async offer(point: string, action?: string): Promise<SparkChallengeOffer | null> {
    let params = new HttpParams().set('point', point);
    if (action) params = params.set('action', action);
    try {
      const envelope = await firstValueFrom(this.http.get<{ result?: SparkChallengeOffer }>(`${this.baseUrl}/offer`, { params }));
      return envelope?.result ?? null;
    } catch (e) {
      if (e instanceof HttpErrorResponse && (e.status === 404 || e.status === 401)) return null;
      throw e;
    }
  }

  /**
   * Solves `offer`. ALTCHA needs no `host`; a hosted provider renders its widget into `host` (the visitor may have to
   * act there) and resolves when the widget hands over its token.
   */
  async solve(offer: SparkChallengeOffer, host?: HTMLElement, signal?: AbortSignal): Promise<SparkChallengeResponse> {
    if (offer.provider === 'altcha') {
      if (!offer.altcha) throw new Error('An ALTCHA offer without a challenge.');
      return { provider: 'altcha', payload: await solveAltcha(offer.altcha, signal) };
    }
    if (!host) throw new Error(`The ${offer.provider} widget needs an element to render into.`);
    if (this.consent && !this.consent.has(THIRD_PARTY_CHALLENGE)) {
      // The server checks consent too; never load a vendor script without it.
      throw new Error('Third-party challenge without consent.');
    }
    if (offer.scriptUrl) await this.loadScript(offer.scriptUrl, offer.provider === 'friendlycaptcha');
    const token = await this.renderWidget(offer, host);
    return { provider: offer.provider, payload: token };
  }

  /** Loads a vendor script once, from the URL the server configured. */
  private loadScript(url: string, module: boolean): Promise<void> {
    let loading = this.scripts.get(url);
    if (!loading) {
      loading = new Promise<void>((resolve, reject) => {
        const script = document.createElement('script');
        script.src = url;
        script.async = true;
        if (module) script.type = 'module';
        script.onload = () => resolve();
        script.onerror = () => {
          this.scripts.delete(url);
          reject(new Error(`Could not load ${url}.`));
        };
        document.head.appendChild(script);
      });
      this.scripts.set(url, loading);
    }
    return loading;
  }

  /** Renders a hosted widget per its vendor's documented API and resolves with its token. */
  private renderWidget(offer: SparkChallengeOffer, host: HTMLElement): Promise<string> {
    const w = globalThis as unknown as Record<string, WidgetApi & Record<string, unknown>>;
    return new Promise<string>((resolve, reject) => {
      const fail = () => reject(new Error(`${offer.provider} did not complete.`));
      switch (offer.provider) {
        case 'turnstile':
          w['turnstile'].render(host, { sitekey: offer.siteKey, action: offer.action ?? undefined, callback: resolve, 'error-callback': fail });
          break;
        case 'hcaptcha':
          w['hcaptcha'].render(host, { sitekey: offer.siteKey, callback: resolve, 'error-callback': fail });
          break;
        case 'recaptcha': {
          const grecaptcha = w['grecaptcha'] as unknown as {
            ready(fn: () => void): void;
            execute?(siteKey: string, options: { action: string }): Promise<string>;
            render(element: HTMLElement, options: Record<string, unknown>): unknown;
          };
          grecaptcha.ready(() => {
            if (offer.action && grecaptcha.execute) grecaptcha.execute(offer.siteKey ?? '', { action: offer.action }).then(resolve, fail);
            else grecaptcha.render(host, { sitekey: offer.siteKey, callback: resolve, 'expired-callback': fail });
          });
          break;
        }
        case 'friendlycaptcha': {
          // Friendly Captcha v2 SDK: a `.frc-captcha` element with `data-sitekey`; it fires `frc:widget.complete`.
          const element = document.createElement('div');
          element.className = 'frc-captcha';
          element.dataset['sitekey'] = offer.siteKey ?? '';
          element.addEventListener('frc:widget.complete', (event: Event) => resolve((event as CustomEvent<{ response: string }>).detail.response));
          element.addEventListener('frc:widget.error', fail);
          host.appendChild(element);
          (w['frcaptcha'] as unknown as { attach?: () => void } | undefined)?.attach?.();
          break;
        }
        default:
          reject(new Error(`Unknown challenge provider ${offer.provider}.`));
      }
    });
  }
}
