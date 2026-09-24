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
