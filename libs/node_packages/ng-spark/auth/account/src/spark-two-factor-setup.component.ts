import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsCheckboxComponent } from '@mintplayer/ng-bootstrap/checkbox';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { SparkAuthService, SparkAuthTranslationService } from '@mintplayer/ng-spark/auth/core';
import { SparkAuthenticatorUri, SparkAccountResult, SparkTwoFactorRequest, SparkTwoFactorState } from '@mintplayer/ng-spark/auth/models';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/auth/pipes';
import { accountMessages } from './account-messages';

/**
 * Two-factor authentication (#460, D16): enroll an authenticator app (server-rendered QR SVG, SP-D),
 * see and regenerate recovery codes, forget this browser, reset the key, disable 2FA; and, when the
 * server offers it and 2FA is on, let an external sign-in skip the code (switching that on needs a code).
 *
 * Built on Identity's `POST /manage/2fa` (which creates the key on the first read) and Spark's read-only
 * `GET /manage/2fa/authenticator-uri`. The QR SVG is shown through an `<img>` data URL, never inserted
 * as markup: an image cannot run script, whatever the SVG contains.
 */
@Component({
  selector: 'spark-two-factor-setup',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, BsAlertComponent, BsCardComponent, BsCardHeaderComponent, BsCheckboxComponent, BsFormComponent, BsFormControlDirective, BsSpinnerComponent, TranslateKeyPipe],
  template: `
    <div class="d-flex justify-content-center">
      <bs-card style="width: 100%; max-width: 640px;">
        <bs-card-header><h3 class="mb-0">{{ 'auth.twoFactorSetupTitle' | t }}</h3></bs-card-header>
        <div class="p-4">
          @for (message of messages(); track message) {
            <bs-alert [type]="colors.danger" class="mb-3 d-block spark-account-error">{{ message | t }}</bs-alert>
          }
          @if (loading()) {
            <div class="text-center"><bs-spinner /></div>
          } @else if (state(); as s) {
            @if (recoveryCodes().length) {
              <bs-alert [type]="colors.warning" class="mb-3 d-block spark-recovery-codes">
                <p class="mb-2">{{ 'auth.recoveryCodesShownOnce' | t }}</p>
                <div class="d-flex flex-wrap gap-3 font-monospace">
                  @for (code of recoveryCodes(); track code) { <span>{{ code }}</span> }
                </div>
              </bs-alert>
            }

            @if (s.isTwoFactorEnabled) {
              <p class="spark-2fa-enabled">{{ 'auth.twoFactorEnabled' | t }}</p>
              <p class="text-muted">{{ 'auth.recoveryCodesLeft' | t }}: {{ s.recoveryCodesLeft }}</p>
              <div class="d-flex flex-wrap gap-2">
                <button type="button" class="btn btn-outline-primary spark-2fa-new-codes" [disabled]="busy()" (click)="regenerateRecoveryCodes()">
                  {{ 'auth.newRecoveryCodes' | t }}
                </button>
                @if (s.isMachineRemembered) {
                  <button type="button" class="btn btn-outline-secondary" [disabled]="busy()" (click)="forgetMachine()">
                    {{ 'auth.forgetBrowser' | t }}
                  </button>
                }
                <button type="button" class="btn btn-outline-warning" [disabled]="busy()" (click)="resetKey()">
                  {{ 'auth.resetAuthenticator' | t }}
                </button>
                <button type="button" class="btn btn-outline-danger spark-2fa-disable" [disabled]="busy()" (click)="disable()">
                  {{ 'auth.disableTwoFactor' | t }}
                </button>
              </div>

              @if (bypassOffered()) {
                <hr class="my-4" />
                <div class="spark-2fa-external-bypass">
                  <bs-form>
                    <bs-checkbox [type]="'checkbox'" [name]="'externalTwoFactorBypass'" [isToggled]="bypassChecked()"
                                 (isToggledChange)="toggleBypass($event === true)">
                      {{ 'auth.externalTwoFactorBypassLabel' | t }}
                    </bs-checkbox>
                  </bs-form>
                  <small class="text-muted d-block">{{ 'auth.externalTwoFactorBypassHint' | t }}</small>
                  @if (bypassPending()) {
                    <bs-form>
                      <form [formGroup]="bypassForm" (ngSubmit)="confirmBypass()" class="d-flex gap-2 align-items-end mt-2">
                        <div class="flex-grow-1">
                          <label for="bypassCode" class="form-label">{{ 'auth.code' | t }}</label>
                          <input type="text" id="bypassCode" formControlName="code" inputmode="numeric" autocomplete="one-time-code" maxlength="8" />
                        </div>
                        <button type="submit" class="btn btn-primary spark-2fa-bypass-confirm" [disabled]="busy()">{{ 'auth.verify' | t }}</button>
                        <button type="button" class="btn btn-outline-secondary spark-2fa-bypass-cancel" [disabled]="busy()" (click)="toggleBypass(false)">{{ 'common.cancel' | t }}</button>
                      </form>
                    </bs-form>
                  }
                </div>
              }
            } @else {
              <p>{{ 'auth.twoFactorSetupIntro' | t }}</p>
              @if (qrDataUrl(); as qr) {
                <div class="text-center my-3">
                  <img class="spark-2fa-qr" [src]="qr" width="200" height="200" [attr.alt]="'auth.qrCodeAlt' | t" />
                </div>
              }
              @if (authenticator(); as a) {
                <p class="text-muted small mb-1">{{ 'auth.manualKey' | t }}</p>
                <p class="font-monospace spark-2fa-key">{{ formatKey(a.sharedKey) }}</p>
              }
              <bs-form>
                <form [formGroup]="form" (ngSubmit)="enable()" class="d-flex gap-2 align-items-end">
                  <div class="flex-grow-1">
                    <label for="twoFactorCode" class="form-label">{{ 'auth.code' | t }}</label>
                    <input type="text" id="twoFactorCode" formControlName="code" inputmode="numeric" autocomplete="one-time-code" maxlength="8" />
                  </div>
                  <button type="submit" class="btn btn-primary spark-2fa-enable" [disabled]="busy()">{{ 'auth.enableTwoFactor' | t }}</button>
                </form>
              </bs-form>
            }
          }
        </div>
      </bs-card>
    </div>
  `,
})
export class SparkTwoFactorSetupComponent {
  private readonly auth = inject(SparkAuthService);
  private readonly translation = inject(SparkAuthTranslationService);
  protected readonly colors = Color;

  readonly form = new FormGroup({ code: new FormControl('', { nonNullable: true }) });

  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly messages = signal<string[]>([]);
  readonly state = signal<SparkTwoFactorState | null>(null);
  readonly authenticator = signal<SparkAuthenticatorUri | null>(null);
  readonly recoveryCodes = signal<string[]>([]);

  /** The server's SVG as an image URL — displayed, never parsed into the DOM. */
  readonly qrDataUrl = computed(() => {
    const svg = this.authenticator()?.qrCodeSvg;
    return svg ? 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svg) : null;
  });

  readonly bypassForm = new FormGroup({ code: new FormControl('', { nonNullable: true }) });
  /** The server offers the external-login bypass (`capabilities.externalLoginTwoFactorBypass`). */
  private readonly bypassCapability = signal(false);
  /** The account's bypass setting, or null until read (or when the read failed). */
  readonly bypass = signal<boolean | null>(null);
  /** Switched on but not yet confirmed with an authenticator code. */
  readonly bypassPending = signal(false);

  /** Shown only when the server offers it AND the account has two-factor on: there is nothing to skip otherwise. */
  readonly bypassOffered = computed(() =>
    this.bypassCapability() && this.bypass() !== null && this.state()?.isTwoFactorEnabled === true);
  readonly bypassChecked = computed(() => this.bypass() === true || this.bypassPending());

  constructor() {
    void this.refresh();
    void Promise.resolve()
      .then(() => this.auth.capabilities())
      .then(capabilities => {
        this.bypassCapability.set(capabilities?.externalLoginTwoFactorBypass === true);
        if (this.bypassCapability()) return this.loadBypass();
        return undefined;
      })
      .catch(() => { /* capability unknown: the toggle stays hidden */ });
  }

  private async loadBypass(): Promise<void> {
    const result = await this.auth.externalLoginTwoFactor();
    this.bypass.set(result.success && result.value ? result.value.bypass : null);
  }

  /** Off: saved at once. On: asks for an authenticator code first ({@link confirmBypass}). */
  async toggleBypass(on: boolean): Promise<void> {
    if (on) {
      if (this.bypass() !== true) this.bypassPending.set(true);
      return;
    }
    if (this.bypassPending()) {
      this.bypassPending.set(false);
      this.bypassForm.reset();
      return;
    }
    if (this.bypass() !== true) return;
    await this.saveBypass(false);
  }

  async confirmBypass(): Promise<void> {
    const code = this.bypassForm.controls.code.value.replace(/\s|-/g, '');
    if (!code) {
      this.messages.set(['auth.enterCodeError']);
      return;
    }
    await this.saveBypass(true, code);
  }

  private async saveBypass(bypass: boolean, code?: string): Promise<void> {
    this.busy.set(true);
    this.messages.set([]);
    try {
      const result = await this.auth.setExternalLoginTwoFactor(bypass, code);
      if (result.success && result.value) {
        this.bypass.set(result.value.bypass);
        this.bypassPending.set(false);
        this.bypassForm.reset();
      } else {
        this.messages.set(result.status === 400 && !result.errors ? ['auth.invalidCode'] : accountMessages(result));
      }
    } finally {
      this.busy.set(false);
    }
  }

  /** Groups the base32 key in fours, the way authenticator apps show it. */
  protected formatKey(key: string): string {
    return key.replace(/(.{4})/g, '$1 ').trim();
  }

  async refresh(): Promise<void> {
    this.loading.set(true);
    const state = await this.auth.twoFactor();
    await this.applyState(state);
    this.loading.set(false);
  }

  async enable(): Promise<void> {
    const code = this.form.controls.code.value.replace(/\s|-/g, '');
    if (!code) {
      this.messages.set(['auth.enterCodeError']);
      return;
    }
    await this.run({ enable: true, twoFactorCode: code });
    if (this.state()?.isTwoFactorEnabled) this.form.reset();
  }

  async disable(): Promise<void> {
    if (!confirm(this.translation.t('auth.confirmDisableTwoFactor'))) return;
    await this.run({ enable: false });
  }

  async regenerateRecoveryCodes(): Promise<void> {
    await this.run({ resetRecoveryCodes: true });
  }

  async forgetMachine(): Promise<void> {
    await this.run({ forgetMachine: true });
    // MapIdentityApi computes isMachineRemembered from the request's own remember-me cookie, which this
    // same response deletes, so it still answers true. The browser is forgotten once the call succeeded.
    const state = this.state();
    if (state?.isMachineRemembered && this.messages().length === 0)
      this.state.set({ ...state, isMachineRemembered: false });
  }

  async resetKey(): Promise<void> {
    if (!confirm(this.translation.t('auth.confirmResetAuthenticator'))) return;
    await this.run({ resetSharedKey: true });
  }

  private async run(request: SparkTwoFactorRequest): Promise<void> {
    this.busy.set(true);
    this.messages.set([]);
    try {
      await this.applyState(await this.auth.twoFactor(request));
    } finally {
      this.busy.set(false);
    }
  }

  private async applyState(result: SparkAccountResult<SparkTwoFactorState>): Promise<void> {
    if (!result.success || !result.value) {
      this.messages.set(result.status === 400 && !result.errors ? ['auth.invalidCode'] : accountMessages(result));
      return;
    }
    const wasEnabled = this.state()?.isTwoFactorEnabled;
    this.state.set(result.value);
    this.recoveryCodes.set(result.value.recoveryCodes ?? []);
    // Disabling two-factor may clear the bypass server-side; read it again when the state flips.
    if (this.bypassCapability() && wasEnabled !== undefined && wasEnabled !== result.value.isTwoFactorEnabled) {
      this.bypassPending.set(false);
      void this.loadBypass();
    }
    if (result.value.isTwoFactorEnabled) {
      this.authenticator.set(null);
      return;
    }
    const uri = await this.auth.authenticatorUri();
    this.authenticator.set(uri.success && uri.value ? uri.value : null);
    if (!uri.success) this.messages.set(accountMessages(uri));
  }
}
