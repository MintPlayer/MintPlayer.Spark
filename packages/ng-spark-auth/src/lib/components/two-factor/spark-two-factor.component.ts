import { Component, inject, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { RouterLink, ActivatedRoute, Router } from '@angular/router';
import { BsFormModule } from '@mintplayer/ng-bootstrap/form';
import { SparkAuthService } from '../../services/spark-auth.service';
import { SPARK_AUTH_CONFIG, SPARK_AUTH_ROUTE_PATHS } from '../../models';

@Component({
  selector: 'spark-two-factor',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, BsFormModule],
  template: `
    <div class="d-flex justify-content-center">
      <div class="card" style="width: 100%; max-width: 400px;">
        <div class="card-body">
          <h3 class="card-title text-center mb-4">Two-Factor Authentication</h3>

          @if (errorMessage()) {
            <div class="alert alert-danger" role="alert">{{ errorMessage() }}</div>
          }

          <bs-form>
            <form [formGroup]="form" (ngSubmit)="onSubmit()">
              @if (!useRecoveryCode()) {
                <div class="mb-3">
                  <label for="code" class="form-label">Authentication Code</label>
                  <input
                    type="text"
                    id="code"
                    formControlName="code"
                    autocomplete="one-time-code"
                    maxlength="6"
                    placeholder="Enter 6-digit code"
                  />
                </div>
              } @else {
                <div class="mb-3">
                  <label for="recoveryCode" class="form-label">Recovery Code</label>
                  <input
                    type="text"
                    id="recoveryCode"
                    formControlName="recoveryCode"
                    autocomplete="off"
                    placeholder="Enter recovery code"
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
                Verify
              </button>
            </form>
          </bs-form>

          <div class="mt-3 text-center">
            <button class="btn btn-link" (click)="toggleRecoveryCode()">
              @if (useRecoveryCode()) {
                Use authentication code instead
              } @else {
                Use a recovery code instead
              }
            </button>
          </div>
          <div class="mt-2 text-center">
            <a [routerLink]="routePaths.login">Back to login</a>
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
      this.errorMessage.set(isRecovery ? 'Please enter a recovery code.' : 'Please enter the 6-digit code.');
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
        this.errorMessage.set('Invalid code. Please try again.');
      },
    });
  }
}

export default SparkTwoFactorComponent;
