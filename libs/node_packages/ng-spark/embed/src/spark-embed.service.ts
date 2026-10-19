import { DOCUMENT } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { inject, Injectable, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { SPARK_CONFIG } from '@mintplayer/ng-spark';
import { SparkEmbedConfig, SparkEmbedHandoverRequest, SparkEmbedHandoverState } from './generated/contracts';
import { SPARK_EMBED_HANDOVER_PARAMETER, SPARK_EMBED_MESSAGES, sparkEmbedOriginAllowed } from './spark-embed-origin';

/** How a hand-over was carried out. `link`: neither a popup nor a top navigation was possible; show the URL as a link. */
export type SparkEmbedHandoverMode = 'popup' | 'parent' | 'top' | 'redirect' | 'link';

export interface SparkEmbedHandoverResult {
  mode: SparkEmbedHandoverMode;
  url: string;
}

/**
 * Embed mode inside the platform's embed routes (commerce X23/GB8).
 *
 * In an iframe on the organiser's site the platform's cookies are third-party and may be blocked, so an embed does
 * reads only, without credentials, and hands over to a top-level window for anything that needs the visitor cookie
 * (X1), a sign-in or a waiting room: {@link handOver}. It reports its height to the `<spark-embed>` loader
 * ({@link startResizeReporting}) and posts only to an allowed embedding origin.
 */
@Injectable({ providedIn: 'root' })
export class SparkEmbedService {
  private readonly http = inject(HttpClient);
  private readonly document = inject(DOCUMENT);
  private readonly baseUrl = (inject(SPARK_CONFIG, { optional: true })?.baseUrl ?? '/spark') + '/embed';
  private configPromise: Promise<SparkEmbedConfig | null> | null = null;

  /** The last hand-over that fell back to a link, for {@link SparkEmbedContinueComponent}. */
  readonly pendingLink = signal<string | null>(null);

  private get window(): Window | null {
    return this.document.defaultView;
  }

  /** Whether this page runs inside a frame. */
  get embedded(): boolean {
    const w = this.window;
    if (!w) return false;
    try {
      return w.self !== w.top;
    } catch {
      return true;
    }
  }

  /** The server's embed configuration; `null` when embed mode is off. */
  config(): Promise<SparkEmbedConfig | null> {
    this.configPromise ??= firstValueFrom(this.http.get<SparkEmbedConfig>(`${this.baseUrl}/config`)).catch(() => null);
    return this.configPromise;
  }

  /**
   * The embedding page's origin when it is allowed: `location.ancestorOrigins` (Chromium, Safari), else the referrer's
   * origin (Firefox). `null` when unknown or not allowed.
   */
  async parentOrigin(): Promise<string | null> {
    const config = await this.config();
    const w = this.window;
    if (!config || !w || !this.embedded) return null;
    const ancestors = (w.location as Location & { ancestorOrigins?: DOMStringList }).ancestorOrigins;
    let candidate: string | null = ancestors && ancestors.length > 0 ? ancestors[0] : null;
    if (!candidate && this.document.referrer) {
      try {
        candidate = new URL(this.document.referrer).origin;
      } catch {
        candidate = null;
      }
    }
    return sparkEmbedOriginAllowed(candidate, config.allowedOrigins) ? candidate : null;
  }

  /** Posts `message` to the embedding page, only if its origin is allowed. */
  async postToParent(message: { type: string } & Record<string, unknown>): Promise<boolean> {
    const origin = await this.parentOrigin();
    const parent = this.window?.parent;
    if (!origin || !parent || parent === this.window) return false;
    parent.postMessage(message, origin);
    return true;
  }

  /**
   * Reports the document's height to the loader whenever it changes. Returns the function that stops it. A no-op
   * outside a frame.
   */
  startResizeReporting(): () => void {
    const w = this.window;
    if (!w || !this.embedded || typeof ResizeObserver === 'undefined') return () => undefined;
    let last = -1;
    const report = () => {
      const height = Math.ceil(this.document.documentElement.scrollHeight);
      if (height === last) return;
      last = height;
      void this.postToParent({ type: SPARK_EMBED_MESSAGES.resize, height });
    };
    const observer = new ResizeObserver(report);
    observer.observe(this.document.documentElement);
    report();
    return () => observer.disconnect();
  }

  /**
   * Continues on the platform, top-level. Call it from a click handler: the popup is opened synchronously, inside the
   * user's gesture, and pointed at the hand-over URL once the server signed it.
   *
   * - `popup` (default): a new window; when the browser blocks it, the loader navigates the organiser's page instead
   *   (`parent`), then the frame tries its top window (`top`), and finally the URL is offered as a link (`link`).
   * - `top`: skips the popup. Waiting-room sales always use it (§4.14): a queue position must survive in a real tab.
   *
   * Outside a frame it simply navigates (`redirect`).
   */
  async handOver(request: SparkEmbedHandoverRequest, prefer: 'popup' | 'top' = 'popup'): Promise<SparkEmbedHandoverResult> {
    const w = this.window;
    const popup = prefer === 'popup' && this.embedded && w ? w.open('', '_blank') : null;
    if (popup) {
      try {
        popup.opener = null;
      } catch {
        // A cross-origin-isolated popup already has no opener.
      }
    }

    let url: string;
    try {
      url = (await firstValueFrom(this.http.post<{ url: string }>(`${this.baseUrl}/handover`, request))).url;
    } catch (e) {
      popup?.close();
      throw e;
    }

    if (!this.embedded) {
      w?.location.assign(url);
      return { mode: 'redirect', url };
    }
    if (popup) {
      popup.location.href = url;
      return { mode: 'popup', url };
    }
    if (await this.postToParent({ type: SPARK_EMBED_MESSAGES.navigate, url })) {
      return { mode: 'parent', url };
    }
    try {
      w!.top!.location.href = url;
      return { mode: 'top', url };
    } catch {
      this.pendingLink.set(url);
      return { mode: 'link', url };
    }
  }

  /** The hand-over token of the current top-level page (`?spark-handover=`), if any. */
  handoverToken(): string | null {
    const search = this.window?.location.search ?? '';
    return new URLSearchParams(search).get(SPARK_EMBED_HANDOVER_PARAMETER);
  }

  /** The state a hand-over carried; `null` when the token is invalid or expired. */
  async redeem(token: string | null = this.handoverToken()): Promise<SparkEmbedHandoverState | null> {
    if (!token) return null;
    try {
      return await firstValueFrom(this.http.post<SparkEmbedHandoverState>(`${this.baseUrl}/handover/redeem`, { token }));
    } catch {
      return null;
    }
  }
}
