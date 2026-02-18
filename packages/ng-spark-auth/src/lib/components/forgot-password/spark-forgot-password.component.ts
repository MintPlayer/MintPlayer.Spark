import { Component, inject, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { SparkAuthService } from '../../services/spark-auth.service';
import { SPARK_AUTH_ROUTE_PATHS } from '../../models';

@Component({
  selector: 'spark-forgot-password',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink],
  template: `
    <div class="d-flex justify-content-center">
      <div class="card" style="width: 100%; max-width: 400px;">
        <div class="card-body">
          <h3 class="card-title text-center mb-4">Forgot Password</h3>

          @if (successMessage()) {
            <div class="alert alert-success" role="alert">{{ successMessage() }}</div>
          }

          @if (errorMessage()) {
            <div class="alert alert-danger" role="alert">{{ errorMessage() }}</div>
          }

          @if (!successMessage()) {
            <p class="text-muted mb-3">
              Enter your email address and we will send you a link to reset your password.
            </p>

            <form [formGroup]="form" (ngSubmit)="onSubmit()">
              <div class="mb-3">
                <label for="email" class="form-label">Email</label>
                <input
                  type="email"
                  id="email"
                  class="form-control"
                  formControlName="email"
                  autocomplete="email"
                />
              </div>

              <button
                type="submit"
                class="btn btn-primary w-100"
                [disabled]="loading()"
              >
                @if (loading()) {
                  <span class="spinner-border spinner-border-sm me-1" role="status"></span>
                }
                Send Reset Link
              </button>
            </form>
          }

          <div class="mt-3 text-center">
            <a [routerLink]="routePaths.login">Back to login</a>
          </div>
        </div>
      </div>
    </div>
  `,
})
export class SparkForgotPasswordComponent {
  private readonly fb = inject(FormBuilder);
  private readonly authService = inject(SparkAuthService);
  readonly routePaths = inject(SPARK_AUTH_ROUTE_PATHS);

  readonly loading = signal(false);
  readonly errorMessage = signal('');
  readonly successMessage = signal('');

  readonly form = this.fb.group({
    email: ['', [Validators.required, Validators.email]],
  });

  onSubmit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.loading.set(true);
    this.errorMessage.set('');
    this.successMessage.set('');

    const { email } = this.form.value;

    this.authService.forgotPassword(email!).subscribe({
      next: () => {
        this.loading.set(false);
        this.successMessage.set(
          'If an account with that email exists, we\'ve sent a password reset link.',
        );
      },
      error: () => {
        this.loading.set(false);
        // Don't reveal whether the email exists
        this.successMessage.set(
          'If an account with that email exists, we\'ve sent a password reset link.',
        );
      },
    });
  }
}

export default SparkForgotPasswordComponent;
