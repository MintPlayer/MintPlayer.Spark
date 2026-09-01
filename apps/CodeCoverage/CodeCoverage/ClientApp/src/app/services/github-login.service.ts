import { Injectable, inject } from '@angular/core';
import { SparkAuthService } from '@mintplayer/ng-spark-auth/core';

export interface GitHubLoginResult {
  success: boolean;
  /** ng-spark-auth error code (e.g. 'popup_closed', 'popup_blocked', 'email_not_verified'). */
  error?: string;
  /** Human-actionable message for the error; consumers decide whether to show it. */
  message?: string;
}

/**
 * The one GitHub login flow, shared by the shell's sign-in button and the
 * home page's "Reconnect GitHub" banner: popup handshake, popup-blocked →
 * full-page redirect fallback, and the error-code → message map. Concurrent
 * invocations share one promise — the popup is a named window, so two flows
 * would otherwise settle on a single postMessage.
 */
@Injectable({ providedIn: 'root' })
export class GitHubLoginService {
  private readonly authService = inject(SparkAuthService);
  private inFlight: Promise<GitHubLoginResult> | null = null;

  /** Server error codes → something a human can act on. */
  private static readonly errorMessages: Record<string, string> = {
    email_not_verified: 'GitHub did not attest a verified email for your account. '
      + 'The GitHub App needs the "Email addresses: Read-only" account permission, '
      + 'and your GitHub primary email must be verified.',
    no_login_info: 'The GitHub authorization was cancelled or expired — try again.',
    account_creation_failed: 'Signing in worked but creating your local account failed — check the server logs.',
    popup_closed: 'The sign-in popup was closed before completing.',
  };

  login(returnUrl = '/home'): Promise<GitHubLoginResult> {
    return this.inFlight ??= this.doLogin(returnUrl).finally(() => (this.inFlight = null));
  }

  // ng-spark-auth 22.1.0 owns the whole popup handshake (blocked, closed and
  // server-refused paths all settle the promise). Fall back to a full-page
  // redirect when a popup blocker interferes — in redirect mode the promise
  // never settles, since the document is being replaced.
  private async doLogin(returnUrl: string): Promise<GitHubLoginResult> {
    const result = await this.authService.loginWithProvider('GitHub', { returnUrl });
    if (result.success) return { success: true };
    if (result.error === 'popup_blocked') {
      await this.authService.loginWithProvider('GitHub', { returnUrl, mode: 'redirect' });
      // Unreachable in practice: the redirect replaces the document.
      return { success: false, error: 'popup_blocked' };
    }
    return {
      success: false,
      error: result.error,
      message: GitHubLoginService.errorMessages[result.error ?? '']
        ?? `Sign-in failed (${result.error ?? 'unknown error'}).`,
    };
  }
}
