import { Injectable, signal } from '@angular/core';

const STORAGE_KEY = 'spark-storefront-admissions';

/**
 * The waiting room's admission tokens by scope (`ticketing-phase:{phaseId}`, §4.12), handed from the waiting room page
 * to the event page, which adds the queued phase's line with it. Kept in `sessionStorage` (this tab only, gone when the
 * tab closes): a token is single-use and short-lived, so it never belongs in a cookie or `localStorage`. Unavailable
 * storage (a private window, blocked site data) degrades to memory.
 */
@Injectable({ providedIn: 'root' })
export class SparkStorefrontAdmissions {
  private readonly state = signal<Record<string, string>>(read());

  /** The tokens by scope. */
  readonly tokens = this.state.asReadonly();

  /** Keeps `token` for `scope`. */
  set(scope: string, token: string): void {
    this.state.set({ ...this.state(), [scope]: token });
    write(this.state());
  }

  /** Forgets `scope`'s token (after it was used, or on a refusal). */
  clear(scope: string): void {
    const { [scope]: _, ...rest } = this.state();
    this.state.set(rest);
    write(rest);
  }
}

function read(): Record<string, string> {
  try {
    const raw = typeof sessionStorage === 'undefined' ? null : sessionStorage.getItem(STORAGE_KEY);
    const parsed = raw ? JSON.parse(raw) : null;
    return parsed && typeof parsed === 'object' ? (parsed as Record<string, string>) : {};
  } catch {
    return {};
  }
}

function write(tokens: Record<string, string>): void {
  try {
    if (typeof sessionStorage !== 'undefined') sessionStorage.setItem(STORAGE_KEY, JSON.stringify(tokens));
  } catch {
    // Storage blocked: the tokens live in memory for this page only.
  }
}
