import { Component, inject, OnInit, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators, AbstractControl, ValidationErrors } from '@angular/forms';
import { RouterLink, ActivatedRoute, Router } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { BsFormModule } from '@mintplayer/ng-bootstrap/form';
import { SparkAuthService } from '../../services/spark-auth.service';
import { SPARK_AUTH_ROUTE_PATHS } from '../../models';

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
  imports: [ReactiveFormsModule, RouterLink, BsFormModule],
  template: `
    <div class="d-flex justify-content-center">
      <div class="card" style="width: 100%; max-width: 400px;">
        <div class="card-body">
          <h3 class="card-title text-center mb-4">Reset Password</h3>

          @if (successMessage()) {
            <div class="alert alert-success" role="alert">
              {{ successMessage() }}
              <div class="mt-2">
                <a [routerLink]="routePaths.login">Go to login</a>
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
                  <label for="newPassword" class="form-label">New Password</label>
                  <input
                    type="password"
                    id="newPassword"
                    formControlName="newPassword"
                    autocomplete="new-password"
                  />
                </div>

                <div class="mb-3">
                  <label for="confirmPassword" class="form-label">Confirm Password</label>
                  <input
                    type="password"
                    id="confirmPassword"
                    formControlName="confirmPassword"
                    autocomplete="new-password"
                  />
                  @if (form.touched && form.hasError('passwordMismatch')) {
                    <div class="text-danger mt-1">Passwords do not match.</div>
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
                  Reset Password
                </button>
              </form>
            </bs-form>

            <div class="mt-3 text-center">
              <a [routerLink]="routePaths.login">Back to login</a>
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
      this.errorMessage.set('Invalid password reset link. Please request a new one.');
    }
  }

  onSubmit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    if (!this.email || !this.code) {
      this.errorMessage.set('Invalid password reset link. Please request a new one.');
      return;
    }

    this.loading.set(true);
    this.errorMessage.set('');
    this.successMessage.set('');

    const { newPassword } = this.form.value;

    this.authService.resetPassword(this.email, this.code, newPassword!).subscribe({
      next: () => {
        this.loading.set(false);
        this.successMessage.set('Your password has been reset successfully.');
      },
      error: (err: HttpErrorResponse) => {
        this.loading.set(false);
        if (err.error?.detail) {
          this.errorMessage.set(err.error.detail);
        } else {
          this.errorMessage.set('Failed to reset password. The link may have expired.');
        }
      },
    });
  }
}

export default SparkResetPasswordComponent;
