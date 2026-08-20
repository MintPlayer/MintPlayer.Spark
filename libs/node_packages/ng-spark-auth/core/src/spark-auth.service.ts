import { computed, inject, Injectable, NgZone, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import {
  AuthUser,
  SPARK_AUTH_CONFIG,
  SparkAuthCapabilities,
  SPARK_EXTERNAL_LOGIN_MESSAGE_TYPE,
  SparkExternalLoginError,
  SparkExternalLoginOptions,
  SparkExternalLoginResult,
} from '@mintplayer/ng-spark-auth/models';

/** How often the popup is checked for a manual close. */
const POPUP_CLOSE_POLL_MS = 400;

@Injectable({ providedIn: 'root' })
export class SparkAuthService {
  private readonly http = inject(HttpClient);
  private readonly config = inject(SPARK_AUTH_CONFIG);
  private readonly zone = inject(NgZone);

  private readonly currentUser = signal<AuthUser | null>(null);

  readonly user = this.currentUser.asReadonly();
  readonly isAuthenticated = computed(() => this.currentUser()?.isAuthenticated === true);

  constructor() {
    this.checkAuth();
  }

  async login(email: string, password: string): Promise<void> {
    await firstValueFrom(this.http.post<void>(`${this.config.apiBasePath}/login?useCookies=true`, { email, password }));
    await this.csrfRefresh();
    await this.checkAuth();
  }

  async loginTwoFactor(twoFactorCode: string, twoFactorRecoveryCode?: string): Promise<void> {
    await firstValueFrom(this.http.post<void>(`${this.config.apiBasePath}/login?useCookies=true`, {
      twoFactorCode,
      twoFactorRecoveryCode,
    }));
    await this.csrfRefresh();
    await this.checkAuth();
  }

  async register(email: string, password: string): Promise<void> {
    await firstValueFrom(this.http.post<void>(`${this.config.apiBasePath}/register`, { email, password }));
  }

  async logout(): Promise<void> {
    await firstValueFrom(this.http.post<void>(`${this.config.apiBasePath}/logout`, {}));
    await this.csrfRefresh();
    this.currentUser.set(null);
  }

  /**
   * What sign-in methods this deployment actually offers.
   *
   * Read from the server rather than assumed from the client's own route configuration: the two are
   * configured independently, and a mismatch is otherwise invisible until a user hits it.
   */
  async capabilities(): Promise<SparkAuthCapabilities> {
    return await firstValueFrom(
      this.http.get<SparkAuthCapabilities>(`${this.config.apiBasePath}/capabilities`));
  }

  async csrfRefresh(): Promise<void> {
    await firstValueFrom(this.http.post<void>(`${this.config.apiBasePath}/csrf-refresh`, {}));
  }

  async checkAuth(): Promise<AuthUser | null> {
    try {
      const user = await firstValueFrom(this.http.get<AuthUser>(`${this.config.apiBasePath}/me`));
      this.currentUser.set(user);
      return user;
    } catch {
      this.currentUser.set(null);
      return null;
    }
  }

  /**
   * Signs in through an external provider (GitHub, Google, …) and leaves this service's
   * `user()` signal up to date, exactly as `login()` does.
   *
   * The whole handshake lives here: opening the window, listening for the callback's
   * message, checking its origin, detecting a popup the user closed by hand, tearing the
   * listener and poll down on every exit path, and re-reading the session afterwards.
   * Callers get an outcome, not a protocol — which is the point, because the previous
   * hand-rolled version leaked its listener whenever the user simply closed the window.
   *
   * In `'redirect'` mode the returned promise never settles: this document is being
   * replaced, and the outcome arrives as the next page load rather than as a value.
   */
  loginWithProvider(provider: string, options: SparkExternalLoginOptions = {}): Promise<SparkExternalLoginResult> {
    const { returnUrl = this.config.defaultRedirectUrl, mode = 'popup' } = options;
    const url = `${this.config.apiBasePath}/external-login?provider=${encodeURIComponent(provider)}`
      + `&returnUrl=${encodeURIComponent(returnUrl)}`;

    if (mode === 'redirect') {
      window.location.assign(url);
      return new Promise<SparkExternalLoginResult>(() => { /* the page is going away */ });
    }

    return new Promise<SparkExternalLoginResult>((resolve) => {
      const popup = window.open(`${url}&popup=1`, 'spark-external-login', 'width=600,height=700');
      if (!popup) {
        resolve({ success: false, error: 'popup_blocked' });
        return;
      }

      // One settle path, so the listener and the poll cannot outlive the flow no matter
      // which of the four ways it ends.
      let settled = false;
      const settle = (error?: SparkExternalLoginError) => {
        if (settled) return;
        settled = true;
        window.removeEventListener('message', onMessage);
        clearInterval(poll);
        popup.close();

        this.zone.run(async () => {
          if (!error) await this.checkAuth();
          resolve(error ? { success: false, error } : { success: true });
        });
      };

      const onMessage = (event: MessageEvent) => {
        if (event.origin !== window.location.origin) return;
        if (event.data?.type !== SPARK_EXTERNAL_LOGIN_MESSAGE_TYPE) return;
        settle(event.data.success ? undefined : (event.data.error ?? 'no_login_info'));
      };

      // A user who closes the window never posts anything, so without this the promise
      // and its listener would both live forever.
      const poll = setInterval(() => {
        if (popup.closed) settle('popup_closed');
      }, POPUP_CLOSE_POLL_MS);

      window.addEventListener('message', onMessage);
    });
  }

  async forgotPassword(email: string): Promise<void> {
    await firstValueFrom(this.http.post<void>(`${this.config.apiBasePath}/forgotPassword`, { email }));
  }

  async resetPassword(email: string, resetCode: string, newPassword: string): Promise<void> {
    await firstValueFrom(this.http.post<void>(`${this.config.apiBasePath}/resetPassword`, {
      email,
      resetCode,
      newPassword,
    }));
  }
}
