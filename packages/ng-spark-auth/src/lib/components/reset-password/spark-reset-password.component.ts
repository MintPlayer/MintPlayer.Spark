import { ChangeDetectionStrategy, Component, inject, OnInit, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators, AbstractControl, ValidationErrors } from '@angular/forms';
import { RouterLink, ActivatedRoute, Router } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { SparkAuthService } from '../../services/spark-auth.service';
import { SPARK_AUTH_ROUTE_PATHS } from '../../models';
import { TranslateKeyPipe } from '../../pipes/translate-key.pipe';
import { SparkAuthTranslationService } from '../../services/spark-auth-translation.service';

function passwordMatchValidator(control: AbstractControl): ValidationErrors | null {
  const password = control.get('newPassword');
  const confirmPassword = control.get('confirmPassword');
  if (password && confirmPassword && password.value !== confirmPassword.value) {
    return { passwordMismatch: true };
  }
  return null;
}

@Component({
  selector: 'spark-reset-password',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, RouterLink, BsFormComponent, BsFormControlDirective, TranslateKeyPipe],
  template: `
    <div class="d-flex justify-content-center">
      <div class="card" style="width: 100%; max-width: 400px;">
        <div class="card-body">
          <h3 class="card-title text-center mb-4">{{ 'authResetPassword' | t }}</h3>

          @if (successMessage()) {
            <div class="alert alert-success" role="alert">
              {{ successMessage() }}
              <div class="mt-2">
                <a [routerLink]="routePaths.login">{{ 'authGoToLogin' | t }}</a>
              </div>
            </div>
          }

          @if (errorMessage()) {
            <div class="alert alert-danger" role="alert">{{ errorMessage() }}</div>
          }

          @if (!successMessage()) {
            <bs-form>
              <form [formGroup]="form" (ngSubmit)="onSubmit()">
                <div class="mb-3">
                  <label for="newPassword" class="form-label">{{ 'authNewPassword' | t }}</label>
                  <input
                    type="password"
                    id="newPassword"
                    formControlName="newPassword"
                    autocomplete="new-password"
                  />
                </div>

                <div class="mb-3">
                  <label for="confirmPassword" class="form-label">{{ 'authConfirmPassword' | t }}</label>
                  <input
                    type="password"
                    id="confirmPassword"
                    formControlName="confirmPassword"
                    autocomplete="new-password"
                  />
                  @if (form.touched && form.hasError('passwordMismatch')) {
                    <div class="text-danger mt-1">{{ 'authPasswordMismatch' | t }}</div>
                  }
                </div>

                <button
                  type="submit"
                  class="btn btn-primary w-100"
                  [disabled]="loading()"
                >
                  @if (loading()) {
                    <span class="spinner-border spinner-border-sm me-1" role="status"></span>
                  }
                  {{ 'authResetPassword' | t }}
                </button>
              </form>
            </bs-form>

            <div class="mt-3 text-center">
              <a [routerLink]="routePaths.login">{{ 'authBackToLogin' | t }}</a>
            </div>
          }
        </div>
      </div>
    </div>
  `,
})
export class SparkResetPasswordComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly authService = inject(SparkAuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly translation = inject(SparkAuthTranslationService);
  readonly routePaths = inject(SPARK_AUTH_ROUTE_PATHS);

  readonly loading = signal(false);
  readonly errorMessage = signal('');
  readonly successMessage = signal('');

  private email = '';
  private code = '';

  readonly form = this.fb.group({
    newPassword: ['', Validators.required],
    confirmPassword: ['', Validators.required],
  }, { validators: passwordMatchValidator });

  ngOnInit(): void {
    this.email = this.route.snapshot.queryParamMap.get('email') ?? '';
    this.code = this.route.snapshot.queryParamMap.get('code') ?? '';

    if (!this.email || !this.code) {
      this.errorMessage.set(this.translation.t('authInvalidResetLink'));
    }
  }

  async onSubmit(): Promise<void> {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    if (!this.email || !this.code) {
      this.errorMessage.set(this.translation.t('authInvalidResetLink'));
      return;
    }

    this.loading.set(true);
    this.errorMessage.set('');
    this.successMessage.set('');

    const { newPassword } = this.form.value;

    try {
      await this.authService.resetPassword(this.email, this.code, newPassword!);
      this.successMessage.set(this.translation.t('authResetSuccess'));
    } catch (err: any) {
      if (err instanceof HttpErrorResponse && err.error?.detail) {
        this.errorMessage.set(err.error.detail);
      } else {
        this.errorMessage.set(this.translation.t('authResetFailed'));
      }
    } finally {
      this.loading.set(false);
    }
  }
}

export default SparkResetPasswordComponent;
