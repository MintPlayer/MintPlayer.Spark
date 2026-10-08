import { computed, inject, Injectable, Injector, NgZone, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import {
  AuthUser,
  SPARK_AUTH_CONFIG,
  SparkAuthCapabilities,
  SPARK_EXTERNAL_LOGIN_ACK_TYPE,
  SPARK_EXTERNAL_LOGIN_CHANNEL,
  SPARK_EXTERNAL_LOGIN_ERRORS,
  SPARK_EXTERNAL_LOGIN_MESSAGE_TYPE,
  SPARK_EXTERNAL_LOGIN_QUERY_PARAM,
  SPARK_EXTERNAL_LOGIN_TIMEOUT_MS,
  SparkExternalLoginAck,
  SparkExternalLoginError,
  SparkExternalLoginMessage,
  SparkExternalLoginOptions,
  SparkExternalLoginResult,
  resolveSparkExternalLoginMode,
  sparkExternalLoginDoneKey,
  sparkExternalLoginNonce,
  sparkExternalLoginStorageKey,
  SparkExternalLogins,
  SparkPasskeyError,
  SparkPasskeyResult,
  passkeysSupported,
  sparkPasskeyError,
  SparkUnlinkResult,
  SparkAccountInfo,
  SparkAccountProfile,
  SparkAccountProfileUpdate,
  SparkAccountResult,
  SparkAuthenticatorUri,
  SparkTwoFactorRequest,
  SparkTwoFactorState,
} from '@mintplayer/ng-spark/auth/models';

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

  /** Resolved lazily: only {@link takeExternalLoginResult} needs the router. */
  private readonly injector = inject(Injector);

  private readonly pendingExternalLogin = signal(false);

  /**
   * `true` while a popup sign-in or link attempt is open and its popup has not been seen closed.
   *
   * ⚠️ Drive button and spinner state from this, **not** from the promise settling. When the
   * provider's login page sends COOP the popup reads closed long before its result arrives, so this
   * turns `false` early (the buttons re-enable) while the attempt keeps listening, and a late result
   * still settles it.
   */
  readonly externalLoginPending = this.pendingExternalLogin.asReadonly();

  /** Settles the open popup attempt; `null` when none is open. */
  private activeExternalAttempt: ((error?: SparkExternalLoginError) => void) | null = null;

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

  /** `userName` is the public handle other users see; it is required and may not contain `@`. */
  async register(email: string, password: string, userName: string): Promise<void> {
    await firstValueFrom(this.http.post<void>(`${this.config.apiBasePath}/register`, { email, password, userName }));
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
   * The whole handshake lives here: a per-attempt nonce, four ways for the result to come back
   * (`postMessage` from the opener-held popup, a BroadcastChannel, a `localStorage` entry and its
   * `storage` event, and a re-read of that entry when this tab is shown again), the acknowledgement
   * that lets the callback page close itself, teardown on every exit path, and re-reading the
   * session afterwards. Callers get an outcome, not a protocol.
   *
   * A popup attempt settles only when its result arrives, when another attempt starts (the old one
   * settles `popup_closed`), or after 10 minutes (`popup_closed`). A popup that *looks* closed does
   * not settle it: the provider's COOP header severs the handle while the user is still signing in.
   * ⚠️ Take button and spinner state from {@link externalLoginPending}, not from this promise.
   *
   * The mode is the call's `mode`, else the configured `externalLoginMode` (`'auto'`: redirect in an
   * installed web app, popup otherwise). In `'redirect'` mode the returned promise never settles:
   * this document is being replaced, and the outcome arrives as the next page load
   * ({@link takeExternalLoginResult}) rather than as a value.
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

  /**
   * Reads the `?sparkExternalLogin=<code>` a redirect-mode round trip came back with, and strips it
   * from the address bar (a `replaceUrl` navigation, so Back does not bring it back and a reload
   * does not show it twice).
   *
   * Answers `null` when the URL carries none. A value that is not a known
   * {@link SparkExternalLoginError} is answered as `no_login_info` rather than echoed, so a crafted
   * link cannot put text of its choosing on the page.
   *
   * Only failures carry the parameter: a successful redirect lands on the `returnUrl` signed in.
   * The shipped sign-in and account pages call this on load. An app whose `returnUrl` is some other
   * page calls it there (or in its shell) to show the failure.
   */
  takeExternalLoginResult(): SparkExternalLoginError | null {
    const router = this.injector.get(Router, null);
    if (!router) return null;
    const tree = router.parseUrl(router.url);
    const raw = tree.queryParams[SPARK_EXTERNAL_LOGIN_QUERY_PARAM];
    if (raw === undefined) return null;

    const { [SPARK_EXTERNAL_LOGIN_QUERY_PARAM]: _, ...rest } = tree.queryParams;
    tree.queryParams = rest;
    // Deferred: this is called from a component constructor, inside the navigation that is
    // activating it; starting another navigation synchronously there would cancel that one.
    queueMicrotask(() => void router.navigateByUrl(tree, { replaceUrl: true }));

    const code = Array.isArray(raw) ? raw[0] : raw;
    return SPARK_EXTERNAL_LOGIN_ERRORS.includes(code as SparkExternalLoginError)
      ? code as SparkExternalLoginError
      : 'no_login_info';
  }

  private externalFlow(
    path: string,
    provider: string,
    options: SparkExternalLoginOptions,
  ): Promise<SparkExternalLoginResult> {
    const returnUrl = options.returnUrl ?? this.config.defaultRedirectUrl;
    const mode = options.mode ?? resolveSparkExternalLoginMode(this.config.externalLoginMode);
    const url = `${this.config.apiBasePath}${path}?provider=${encodeURIComponent(provider)}`
      + `&returnUrl=${encodeURIComponent(returnUrl)}`;

    // D2: a new attempt supersedes the open one, which settles 'popup_closed' and is torn down.
    this.activeExternalAttempt?.('popup_closed');

    if (mode === 'redirect') {
      window.location.assign(url);
      return new Promise<SparkExternalLoginResult>(() => { /* the page is going away */ });
    }

    const nonce = sparkExternalLoginNonce();
    const popup = window.open(
      `${url}&popup=1&nonce=${nonce}&ngsw-bypass=true`, 'spark-external-login', 'width=600,height=700');
    // No automatic redirect fallback (F10): in Firefox's installed web app a null handle may still
    // have started the flow inside the app, and redirecting too would run a second one. The shipped
    // sign-in page offers "Continue in this tab" instead.
    if (!popup) return Promise.resolve({ success: false, error: 'popup_blocked' });

    return new Promise<SparkExternalLoginResult>((resolve) => {
      const storageKey = sparkExternalLoginStorageKey(nonce);
      const doneKey = sparkExternalLoginDoneKey(nonce);

      let channel: BroadcastChannel | null = null;
      try {
        channel = typeof BroadcastChannel === 'function' ? new BroadcastChannel(SPARK_EXTERNAL_LOGIN_CHANNEL) : null;
      } catch {
        channel = null;
      }

      // One settle path, so no listener, channel or timer outlives the attempt whichever way it ends.
      let settled = false;
      const settle = (error?: SparkExternalLoginError) => {
        if (settled) return;
        settled = true;
        window.removeEventListener('message', onMessage);
        window.removeEventListener('storage', onStorage);
        window.removeEventListener('focus', reread);
        document.removeEventListener('visibilitychange', onVisibility);
        if (channel) {
          channel.removeEventListener('message', onChannel);
          channel.close();
        }
        clearInterval(poll);
        clearTimeout(timeout);
        if (this.activeExternalAttempt === settle) {
          this.activeExternalAttempt = null;
          this.pendingExternalLogin.set(false);
        }

        this.zone.run(async () => {
          if (!error) {
            // Explicit, like login(): the identity just changed, so ask for a token bound to it
            // rather than relying on the server minting one on whatever response comes next.
            // Never fatal — the sign-in already happened, and a rejection here would leave this
            // promise unresolved.
            await this.csrfRefresh().catch(() => undefined);
            await this.checkAuth();
          }
          resolve(error ? { success: false, error } : { success: true });
        });
      };

      /** The first payload for this nonce wins, from whichever of the four sources brings it. */
      const deliver = (data: unknown) => {
        if (settled) return;
        const message = data as Partial<SparkExternalLoginMessage> | null;
        if (!message || typeof message !== 'object') return;
        if (message.type !== SPARK_EXTERNAL_LOGIN_MESSAGE_TYPE) return;
        // A payload for another attempt (another tab, an older popup) is not ours.
        if (message.nonce !== nonce) return;

        const ack: SparkExternalLoginAck = { type: SPARK_EXTERNAL_LOGIN_ACK_TYPE, nonce };
        try { channel?.postMessage(ack); } catch { /* the callback page falls back to its own close */ }
        try {
          localStorage.setItem(doneKey, '1');
          localStorage.removeItem(storageKey);
        } catch { /* storage may be unavailable (private mode); the result is already in hand */ }
        try { popup.close(); } catch { /* cut off by COOP: nothing to close from here */ }

        settle(message.success ? undefined : (message.error ?? 'no_login_info'));
      };

      const onMessage = (event: MessageEvent) => {
        if (event.origin !== window.location.origin) return;
        deliver(event.data);
      };
      const onChannel = (event: MessageEvent) => deliver(event.data);
      const readStored = () => {
        let stored: string | null = null;
        try { stored = localStorage.getItem(storageKey); } catch { return; }
        if (!stored) return;
        try { deliver(JSON.parse(stored)); } catch { /* not a payload */ }
      };
      const onStorage = (event: StorageEvent) => {
        if (event.key === storageKey && event.newValue) readStored();
      };
      // A frozen background tab may have missed both the channel and the storage event.
      const reread = () => readStored();
      const onVisibility = () => {
        if (document.visibilityState === 'visible') readStored();
      };

      // D2 final: the poll drives UI only. Under COOP (F6) the popup reads closed while the user is
      // still signing in, so a closed popup only re-enables the buttons; the listeners stay.
      const poll = setInterval(() => {
        let closed = false;
        try { closed = popup.closed; } catch { closed = false; }
        if (!closed) return;
        clearInterval(poll);
        if (this.activeExternalAttempt === settle) this.pendingExternalLogin.set(false);
      }, POPUP_CLOSE_POLL_MS);
      const timeout = setTimeout(() => settle('popup_closed'), SPARK_EXTERNAL_LOGIN_TIMEOUT_MS);

      window.addEventListener('message', onMessage);
      window.addEventListener('storage', onStorage);
      window.addEventListener('focus', reread);
      document.addEventListener('visibilitychange', onVisibility);
      channel?.addEventListener('message', onChannel);

      this.activeExternalAttempt = settle;
      this.pendingExternalLogin.set(true);
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
  // Sign-in is two round trips: ask the server for options, hand them to the authenticator, post what
  // it produced back. The server keeps the challenge itself — nothing below carries it, and nothing
  // below may start to, because a client that can choose its own challenge can replay an assertion.
  //
  // Managing the signed-in user's passkeys is not here: it is the generic passkeys page
  // (`/po/passkeys/me`), whose Add action runs the enrollment ceremony through the `webauthn.create`
  // client method (`sparkAuthClientMethods`).
  // ---------------------------------------------------------------------------------------------

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

  /** {@link sparkPasskeyError}; shared with the `webauthn.create` client method. */
  private passkeyError(error: unknown): SparkPasskeyError {
    return sparkPasskeyError(error);
  }
}
