import { Component, inject, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { RouterLink, ActivatedRoute, Router } from '@angular/router';
import { BsFormModule } from '@mintplayer/ng-bootstrap/form';
import { SparkAuthService } from '../../services/spark-auth.service';
import { SPARK_AUTH_CONFIG, SPARK_AUTH_ROUTE_PATHS } from '../../models';
import { TranslateKeyPipe } from '../../pipes/translate-key.pipe';
import { SparkAuthTranslationService } from '../../services/spark-auth-translation.service';

@Component({
  selector: 'spark-two-factor',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, BsFormModule, TranslateKeyPipe],
  template: `
    <div class="d-flex justify-content-center">
      <div class="card" style="width: 100%; max-width: 400px;">
        <div class="card-body">
          <h3 class="card-title text-center mb-4">{{ 'authTwoFactorTitle' | t }}</h3>

          @if (errorMessage()) {
            <div class="alert alert-danger" role="alert">{{ errorMessage() }}</div>
          }

          <bs-form>
            <form [formGroup]="form" (ngSubmit)="onSubmit()">
              @if (!useRecoveryCode()) {
                <div class="mb-3">
                  <label for="code" class="form-label">{{ 'authCode' | t }}</label>
                  <input
                    type="text"
                    id="code"
                    formControlName="code"
                    autocomplete="one-time-code"
                    maxlength="6"
                    [placeholder]="'authEnterCode' | t"
                  />
                </div>
              } @else {
                <div class="mb-3">
                  <label for="recoveryCode" class="form-label">{{ 'authRecoveryCode' | t }}</label>
                  <input
                    type="text"
                    id="recoveryCode"
                    formControlName="recoveryCode"
                    autocomplete="off"
                    [placeholder]="'authEnterRecoveryCode' | t"
                  />
                </div>
              }

              <button
                type="submit"
                class="btn btn-primary w-100"
                [disabled]="loading()"
              >
                @if (loading()) {
                  <span class="spinner-border spinner-border-sm me-1" role="status"></span>
                }
                {{ 'authVerify' | t }}
              </button>
            </form>
          </bs-form>

          <div class="mt-3 text-center">
            <button class="btn btn-link" (click)="toggleRecoveryCode()">
              @if (useRecoveryCode()) {
                {{ 'authUseAuthCode' | t }}
              } @else {
                {{ 'authUseRecoveryCode' | t }}
              }
            </button>
          </div>
          <div class="mt-2 text-center">
            <a [routerLink]="routePaths.login">{{ 'authBackToLogin' | t }}</a>
          </div>
        </div>
      </div>
    </div>
  `,
})
export class SparkTwoFactorComponent {
  private readonly fb = inject(FormBuilder);
  private readonly authService = inject(SparkAuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly config = inject(SPARK_AUTH_CONFIG);
  private readonly translation = inject(SparkAuthTranslationService);
  readonly routePaths = inject(SPARK_AUTH_ROUTE_PATHS);

  readonly loading = signal(false);
  readonly errorMessage = signal('');
  readonly useRecoveryCode = signal(false);

  readonly form = this.fb.group({
    code: [''],
    recoveryCode: [''],
  });

  toggleRecoveryCode(): void {
    this.useRecoveryCode.update(v => !v);
    this.errorMessage.set('');
  }

  onSubmit(): void {
    const isRecovery = this.useRecoveryCode();
    const code = isRecovery ? this.form.value.recoveryCode : this.form.value.code;

    if (!code?.trim()) {
      this.errorMessage.set(isRecovery
        ? this.translation.t('authEnterRecoveryCodeError')
        : this.translation.t('authEnterCodeError'));
      return;
    }

    this.loading.set(true);
    this.errorMessage.set('');

    const twoFactorCode = isRecovery ? undefined : code;
    const twoFactorRecoveryCode = isRecovery ? code : undefined;

    this.authService.loginTwoFactor(twoFactorCode ?? '', twoFactorRecoveryCode).subscribe({
      next: () => {
        this.loading.set(false);
        const returnUrl = this.route.snapshot.queryParamMap.get('returnUrl');
        this.router.navigateByUrl(returnUrl || this.config.defaultRedirectUrl);
      },
      error: () => {
        this.loading.set(false);
        this.errorMessage.set(this.translation.t('authInvalidCode'));
      },
    });
  }
}

export default SparkTwoFactorComponent;
