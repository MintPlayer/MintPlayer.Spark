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
  SparkExternalLogins,
  SparkPasskey,
  SparkPasskeyError,
  SparkPasskeyRegistrationResult,
  SparkPasskeyResult,
  passkeysSupported,
  SparkUnlinkResult,
  SparkAccountInfo,
  SparkAccountProfile,
  SparkAccountProfileUpdate,
  SparkAccountResult,
  SparkAuthenticatorUri,
  SparkTwoFactorRequest,
  SparkTwoFactorState,
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

  /**
   * The credentials of a sign-in that answered `RequiresTwoFactor`, held in memory only until the
   * second step. MapIdentityApi's `/login` is stateless per request: the 2FA step must repeat the
   * email and password next to the code (its `LoginRequest` requires both), or it answers 400.
   */
  private pendingTwoFactorLogin: { email: string; password: string } | null = null;

  /** Whether the pending sign-in (and its two-factor step) asked for a persistent cookie. */
  private pendingRememberMe = true;

  /**
   * The login URL. MapIdentityApi's `/login` issues a persistent cookie for `useCookies=true`
   * and a session cookie (no `Expires`) when `useSessionCookies=true` is added.
   */
  private loginUrl(rememberMe: boolean): string {
    return `${this.config.apiBasePath}/login?useCookies=true${rememberMe ? '' : '&useSessionCookies=true'}`;
  }

  /**
   * Signs in with a cookie. `rememberMe` (default `true`) makes the cookie persistent; `false`
   * makes it a session cookie that ends with the browser session.
   */
  async login(email: string, password: string, rememberMe = true): Promise<void> {
    this.pendingTwoFactorLogin = null;
    this.pendingRememberMe = rememberMe;
    try {
      await firstValueFrom(this.http.post<void>(this.loginUrl(rememberMe), { email, password }));
    } catch (err: any) {
      if (err?.status === 401 && err?.error?.detail === 'RequiresTwoFactor')
        this.pendingTwoFactorLogin = { email, password };
      throw err;
    }
    await this.csrfRefresh();
    await this.checkAuth();
  }

  async loginTwoFactor(twoFactorCode: string, twoFactorRecoveryCode?: string): Promise<void> {
    await firstValueFrom(this.http.post<void>(this.loginUrl(this.pendingRememberMe), {
      ...this.pendingTwoFactorLogin,
      twoFactorCode,
      twoFactorRecoveryCode,
    }));
    this.pendingRememberMe = true;
    this.pendingTwoFactorLogin = null;
    await this.csrfRefresh();
    await this.checkAuth();
  }

  async register(email: string, password: string): Promise<void> {
    await firstValueFrom(this.http.post<void>(`${this.config.apiBasePath}/register`, { email, password }));
  }

  async logout(): Promise<void> {
    this.pendingTwoFactorLogin = null;
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
    return this.externalFlow('/external-login', provider, options);
  }

  /**
   * What external logins are attached to the signed-in account, and what else could be.
   *
   * Only served when the deployment allows linking at all; under `Disabled` the endpoint is not
   * mapped, so this rejects with a 404. That is deliberate on the server side — an account page
   * offering an action the deployment forbids is worse than one that is absent.
   */
  async externalLogins(): Promise<SparkExternalLogins> {
    return await firstValueFrom(
      this.http.get<SparkExternalLogins>(`${this.config.apiBasePath}/external-logins`));
  }

  /**
   * Attaches another provider to the account that is already signed in.
   *
   * The same handshake as {@link loginWithProvider}, against the endpoint that links rather than
   * the one that signs in — the difference that matters is on the server, where the challenge is
   * keyed on the current user so the identity coming back cannot land in somebody else's session.
   */
  linkProvider(provider: string, options: SparkExternalLoginOptions = {}): Promise<SparkExternalLoginResult> {
    return this.externalFlow('/external-logins/link', provider, options);
  }

  /**
   * Detaches a provider from the signed-in account.
   *
   * ⚠️ Resolves with `error: 'last_credential'` rather than throwing when the server refuses to
   * remove the account's last way in. It is an expected answer to a reasonable request, not a
   * fault — and it is the server's refusal that protects the user, not the `canUnlink` flag the
   * list hands out.
   */
  async unlinkProvider(provider: string, providerKey: string): Promise<SparkUnlinkResult> {
    const url = `${this.config.apiBasePath}/external-logins/unlink`
      + `?provider=${encodeURIComponent(provider)}`
      + `&providerKey=${encodeURIComponent(providerKey)}`;
    try {
      await firstValueFrom(this.http.post<{ unlinked: boolean }>(url, {}));
      await this.checkAuth();
      return { success: true };
    } catch (response: unknown) {
      const error = (response as { error?: { error?: SparkUnlinkResult['error'] } })?.error?.error;
      return { success: false, error: error ?? 'unlink_failed' };
    }
  }

  private externalFlow(
    path: string,
    provider: string,
    options: SparkExternalLoginOptions,
  ): Promise<SparkExternalLoginResult> {
    const { returnUrl = this.config.defaultRedirectUrl, mode = 'popup' } = options;
    const url = `${this.config.apiBasePath}${path}?provider=${encodeURIComponent(provider)}`
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

  // ---------------------------------------------------------------------------------------------
  // Account management (#460, D16) — the endpoints behind withAccount()'s pages. Every call resolves
  // a SparkAccountResult instead of throwing: a refusal (a wrong password, an invalid culture, a
  // missing re-authentication) is an expected answer the page renders, not a fault.
  // ---------------------------------------------------------------------------------------------

  /** `POST /confirm-email` — the query of a mailed confirmation link (a changed email too). */
  confirmEmail(userId: string, code: string, changedEmail?: string | null): Promise<SparkAccountResult> {
    return this.accountCall(async () => {
      await firstValueFrom(this.http.post<void>(`${this.config.apiBasePath}/confirm-email`, {
        userId, code, ...(changedEmail ? { changedEmail } : {}),
      }));
      // A signed-in user's claims carry the email; re-read them after a change.
      if (this.isAuthenticated()) await this.checkAuth();
    });
  }

  /** `POST /manage/password` — change it (`currentPassword` required when the account has one) or set a first one. */
  setPassword(newPassword: string, currentPassword?: string | null): Promise<SparkAccountResult> {
    return this.accountCall(async () => {
      await firstValueFrom(this.http.post<void>(`${this.config.apiBasePath}/manage/password`, {
        newPassword, ...(currentPassword ? { currentPassword } : {}),
      }));
    });
  }

  /** `GET /manage/info` — the email and whether it is confirmed. */
  accountInfo(): Promise<SparkAccountResult<SparkAccountInfo>> {
    return this.accountCall(() => firstValueFrom(this.http.get<SparkAccountInfo>(`${this.config.apiBasePath}/manage/info`)));
  }

  /**
   * `POST /manage/info { newEmail }` — mails a confirmation link to the NEW address; nothing changes
   * until it is followed (the confirm-email page completes it).
   */
  changeEmail(newEmail: string): Promise<SparkAccountResult<SparkAccountInfo>> {
    return this.accountCall(() =>
      firstValueFrom(this.http.post<SparkAccountInfo>(`${this.config.apiBasePath}/manage/info`, { newEmail })));
  }

  /** `GET /manage/profile`. */
  profile(): Promise<SparkAccountResult<SparkAccountProfile>> {
    return this.accountCall(() => firstValueFrom(this.http.get<SparkAccountProfile>(`${this.config.apiBasePath}/manage/profile`)));
  }

  /** `POST /manage/profile` — answers the saved profile, or field errors. */
  updateProfile(update: SparkAccountProfileUpdate): Promise<SparkAccountResult<SparkAccountProfile>> {
    return this.accountCall(async () => {
      const saved = await firstValueFrom(this.http.post<SparkAccountProfile>(`${this.config.apiBasePath}/manage/profile`, update));
      if (update.userName !== undefined) await this.checkAuth();
      return saved;
    });
  }

  /** `POST /manage/2fa` (Identity's) — an empty request reads the state and creates a key when none exists. */
  twoFactor(request: SparkTwoFactorRequest = {}): Promise<SparkAccountResult<SparkTwoFactorState>> {
    return this.accountCall(() =>
      firstValueFrom(this.http.post<SparkTwoFactorState>(`${this.config.apiBasePath}/manage/2fa`, request)));
  }

  /** `GET /manage/2fa/authenticator-uri` — 409 `no_authenticator_key` until {@link twoFactor} created one. */
  authenticatorUri(): Promise<SparkAccountResult<SparkAuthenticatorUri>> {
    return this.accountCall(() =>
      firstValueFrom(this.http.get<SparkAuthenticatorUri>(`${this.config.apiBasePath}/manage/2fa/authenticator-uri`)));
  }

  /** `GET /manage/personal-data` — the account and every contributor's data, as JSON. */
  personalData(): Promise<SparkAccountResult<unknown>> {
    return this.accountCall(() => firstValueFrom(this.http.get<unknown>(`${this.config.apiBasePath}/manage/personal-data`)));
  }

  /**
   * `DELETE /manage/account` — re-authenticated by `password`, or by a sign-in younger than the server's
   * `ReauthenticationMaxAge` (5 minutes) when omitted. 403 `reauthentication_required` otherwise. On
   * success the session is gone.
   */
  deleteAccount(password?: string | null): Promise<SparkAccountResult> {
    return this.accountCall(async () => {
      await firstValueFrom(this.http.delete<void>(`${this.config.apiBasePath}/manage/account`, {
        body: password ? { password } : {},
      }));
      this.currentUser.set(null);
      // The antiforgery token was bound to the deleted identity.
      await this.csrfRefresh().catch(() => undefined);
    });
  }

  private async accountCall<T>(call: () => Promise<T>): Promise<SparkAccountResult<T>> {
    try {
      const value = await call();
      return { success: true, value };
    } catch (response: unknown) {
      const http = response as { status?: number; error?: unknown };
      const body = (http?.error ?? null) as { error?: unknown; errors?: Record<string, string[] | string> } | null;
      const errors: Record<string, string[]> = {};
      if (body && typeof body === 'object' && body.errors && typeof body.errors === 'object') {
        for (const [key, value] of Object.entries(body.errors)) errors[key] = Array.isArray(value) ? value : [String(value)];
      }
      return {
        success: false,
        status: http?.status,
        error: body && typeof body === 'object' && typeof body.error === 'string' ? body.error : undefined,
        errors: Object.keys(errors).length ? errors : undefined,
      };
    }
  }

  // ---------------------------------------------------------------------------------------------
  // Passkeys (WebAuthn)
  //
  // Both ceremonies are two round trips: ask the server for options, hand them to the authenticator,
  // post what it produced back. The server keeps the challenge itself — nothing below carries it, and
  // nothing below may start to, because a client that can choose its own challenge can replay an
  // assertion and enroll a credential onto somebody else's account.
  // ---------------------------------------------------------------------------------------------

  /**
   * Enrolls a passkey against the signed-in account.
   *
   * Requires an existing session: this adds a credential to an account, it does not create one.
   */
  async registerPasskey(name?: string): Promise<SparkPasskeyRegistrationResult> {
    if (!passkeysSupported()) return { success: false, error: 'unsupported' };

    try {
      const optionsJson = await firstValueFrom(
        this.http.post(`${this.config.apiBasePath}/passkeys/creation-options`, {}, { responseType: 'text' }));

      const options = PublicKeyCredential.parseCreationOptionsFromJSON(JSON.parse(optionsJson));
      const credential = await navigator.credentials.create({ publicKey: options });
      if (!credential) return { success: false, error: 'no_credential' };

      const passkey = await firstValueFrom(
        this.http.post<SparkPasskey>(`${this.config.apiBasePath}/passkeys`, {
          credentialJson: JSON.stringify(credential),
          name,
        }));

      await this.csrfRefresh();
      return { success: true, passkey };
    } catch (error) {
      return { success: false, error: this.passkeyError(error) };
    }
  }

  /**
   * Signs in with a passkey.
   *
   * ⚠️ Takes no username, and must not grow one. The server's request-options endpoint refuses to
   * accept an identity precisely so that its response cannot reveal whether an account exists; the
   * browser offers whatever discoverable credential it holds, and the account is resolved from the
   * credential afterwards.
   */
  async signInWithPasskey(): Promise<SparkPasskeyResult> {
    if (!passkeysSupported()) return { success: false, error: 'unsupported' };

    try {
      const optionsJson = await firstValueFrom(
        this.http.post(`${this.config.apiBasePath}/passkeys/request-options`, {}, { responseType: 'text' }));

      const options = PublicKeyCredential.parseRequestOptionsFromJSON(JSON.parse(optionsJson));
      const credential = await navigator.credentials.get({ publicKey: options });
      if (!credential) return { success: false, error: 'no_credential' };

      await firstValueFrom(
        this.http.post<void>(`${this.config.apiBasePath}/passkeys/sign-in`, {
          credentialJson: JSON.stringify(credential),
        }));

      await this.csrfRefresh();
      await this.checkAuth();
      return { success: true };
    } catch (error) {
      return { success: false, error: this.passkeyError(error) };
    }
  }

  async passkeys(): Promise<SparkPasskey[]> {
    return await firstValueFrom(this.http.get<SparkPasskey[]>(`${this.config.apiBasePath}/passkeys`));
  }

  async renamePasskey(id: string, name: string): Promise<SparkPasskeyResult> {
    try {
      await firstValueFrom(
        this.http.post<SparkPasskey>(`${this.config.apiBasePath}/passkeys/${encodeURIComponent(id)}/name`, { name }));
      return { success: true };
    } catch (error) {
      return { success: false, error: this.passkeyError(error) };
    }
  }

  /**
   * Removes a passkey.
   *
   * Resolves `{ success: false, error: 'last_credential' }` rather than throwing when it was the
   * account's only way in — the same shape `unlinkProvider` already uses, so a caller handles both
   * the same way.
   */
  async removePasskey(id: string): Promise<SparkPasskeyResult> {
    try {
      await firstValueFrom(
        this.http.delete<void>(`${this.config.apiBasePath}/passkeys/${encodeURIComponent(id)}`));
      return { success: true };
    } catch (error) {
      return { success: false, error: this.passkeyError(error) };
    }
  }

  /**
   * Collapses everything that can go wrong into the closed error union.
   *
   * `AbortError` and `NotAllowedError` are how a browser reports "the user dismissed the prompt" and
   * "no credential was produced" — neither is a fault, and neither should surface as a red banner.
   */
  private passkeyError(error: unknown): SparkPasskeyError {
    if (error instanceof DOMException) {
      if (error.name === 'AbortError') return 'cancelled';
      if (error.name === 'NotAllowedError') return 'no_credential';
      return 'failed';
    }

    const code = (error as { error?: { error?: string } })?.error?.error;
    if (code === 'locked_out') return 'locked_out';
    if (code === 'last_credential') return 'last_credential';

    return 'failed';
  }
}
