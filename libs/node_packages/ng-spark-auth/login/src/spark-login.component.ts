import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { RouterLink, ActivatedRoute, Router } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsCheckboxComponent } from '@mintplayer/ng-bootstrap/checkbox';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { SPARK_AUTH_CONFIG, SPARK_AUTH_ROUTE_PATHS, sanitizeReturnUrl, passkeysSupported } from '@mintplayer/ng-spark-auth/models';
import { SparkAuthService, SparkAuthTranslationService } from '@mintplayer/ng-spark-auth/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark-auth/pipes';

@Component({
  selector: 'spark-login',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, RouterLink, BsAlertComponent, BsCardComponent, BsCardHeaderComponent, BsFormComponent, BsFormControlDirective, BsCheckboxComponent, BsSpinnerComponent, TranslateKeyPipe],
  templateUrl: './spark-login.component.html',
})
export class SparkLoginComponent {
  private readonly fb = inject(FormBuilder);
  private readonly authService = inject(SparkAuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly config = inject(SPARK_AUTH_CONFIG);
  private readonly translation = inject(SparkAuthTranslationService);
  readonly routePaths = inject(SPARK_AUTH_ROUTE_PATHS);

  colors = Color;
  readonly loading = signal(false);
  readonly errorMessage = signal('');

  /**
   * The passkey button belongs here as well as on the sign-in landing page.
   *
   * ⚠️ Without it an application using `withLocalLogin()` can *enroll* a passkey and then have no way
   * to use it, because the landing page it would otherwise appear on is only mounted by
   * `withExternalLogin()`. Passkeys are independent of both features, so the button has to be
   * reachable from whichever sign-in page the application actually mounted.
   */
  readonly passkeysAvailable = signal(false);
  readonly passkeyBusy = signal(false);

  constructor() {
    void this.loadCapabilities();
  }

  private async loadCapabilities(): Promise<void> {
    try {
      const capabilities = await this.authService.capabilities();
      this.passkeysAvailable.set(capabilities.passkeys === true && passkeysSupported());
    } catch {
      // A capability lookup that fails must not break password sign-in, which is this page's job.
      this.passkeysAvailable.set(false);
    }
  }

  /** Same shape as {@link onSubmit}'s success path — sign in, then leave the page. */
  async signInWithPasskey(): Promise<void> {
    this.errorMessage.set('');
    this.passkeyBusy.set(true);
    try {
      const returnUrl = sanitizeReturnUrl(
        this.route.snapshot.queryParamMap.get('returnUrl'), this.config.defaultRedirectUrl);
      const result = await this.authService.signInWithPasskey();

      if (result.success) {
        await this.router.navigateByUrl(returnUrl);
        return;
      }

      // Dismissing the prompt is "not now", not a failure.
      if (result.error === 'cancelled') return;

      this.errorMessage.set(this.translation.t(
        result.error === 'locked_out' ? 'auth.lockedOut' : 'auth.passkeyFailed'));
    } finally {
      this.passkeyBusy.set(false);
    }
  }

  readonly form = this.fb.group({
    email: ['', Validators.required],
    password: ['', Validators.required],
    rememberMe: [false],
  });

  async onSubmit(): Promise<void> {
    if (this.form.invalid) return;

    this.loading.set(true);
    this.errorMessage.set('');

    const { email, password } = this.form.value;

    try {
      await this.authService.login(email!, password!);
      // R2-H9: validate returnUrl against the local-path rule. navigateByUrl
      // accepts some external shapes (protocol-relative '//attacker'); reject
      // and fall back to the default to close the open-redirect.
      const returnUrl = sanitizeReturnUrl(
        this.route.snapshot.queryParamMap.get('returnUrl'),
        this.config.defaultRedirectUrl);
      this.router.navigateByUrl(returnUrl);
    } catch (err: any) {
      if (err instanceof HttpErrorResponse && err.status === 401 && err.error?.detail === 'RequiresTwoFactor') {
        // Forward the same sanitized returnUrl to the 2FA step so the user
        // lands on the intended in-app page after completing the challenge.
        const returnUrl = sanitizeReturnUrl(
          this.route.snapshot.queryParamMap.get('returnUrl'),
          this.config.defaultRedirectUrl);
        this.router.navigate([this.routePaths.twoFactor], {
          queryParams: { returnUrl },
        });
      } else {
        this.errorMessage.set(this.translation.t('auth.invalidCredentials'));
      }
    } finally {
      this.loading.set(false);
    }
  }
}

export default SparkLoginComponent;
