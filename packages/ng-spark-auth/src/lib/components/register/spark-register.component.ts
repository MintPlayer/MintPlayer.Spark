import { Component, inject, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators, AbstractControl, ValidationErrors } from '@angular/forms';
import { RouterLink, Router } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { BsFormModule } from '@mintplayer/ng-bootstrap/form';
import { SparkAuthService } from '../../services/spark-auth.service';
import { SPARK_AUTH_ROUTE_PATHS } from '../../models';

function passwordMatchValidator(control: AbstractControl): ValidationErrors | null {
  const password = control.get('password');
  const confirmPassword = control.get('confirmPassword');
  if (password && confirmPassword && password.value !== confirmPassword.value) {
    return { passwordMismatch: true };
  }
  return null;
}

@Component({
  selector: 'spark-register',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, BsFormModule],
  template: `
    <div class="d-flex justify-content-center">
      <div class="card" style="width: 100%; max-width: 400px;">
        <div class="card-body">
          <h3 class="card-title text-center mb-4">Register</h3>

          @if (errorMessage()) {
            <div class="alert alert-danger" role="alert">{{ errorMessage() }}</div>
          }

          <bs-form>
            <form [formGroup]="form" (ngSubmit)="onSubmit()">
              <div class="mb-3">
                <label for="email" class="form-label">Email</label>
                <input
                  type="email"
                  id="email"
                  formControlName="email"
                  autocomplete="email"
                />
                @if (form.get('email')?.touched && form.get('email')?.hasError('email')) {
                  <div class="text-danger mt-1">Please enter a valid email address.</div>
                }
              </div>

              <div class="mb-3">
                <label for="password" class="form-label">Password</label>
                <input
                  type="password"
                  id="password"
                  formControlName="password"
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
                Register
              </button>
            </form>
          </bs-form>

          <div class="mt-3 text-center">
            <span>Already have an account? </span>
            <a [routerLink]="routePaths.login">Login</a>
          </div>
        </div>
      </div>
    </div>
  `,
})
export class SparkRegisterComponent {
  private readonly fb = inject(FormBuilder);
  private readonly authService = inject(SparkAuthService);
  private readonly router = inject(Router);
  readonly routePaths = inject(SPARK_AUTH_ROUTE_PATHS);

  readonly loading = signal(false);
  readonly errorMessage = signal('');

  readonly form = this.fb.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', Validators.required],
    confirmPassword: ['', Validators.required],
  }, { validators: passwordMatchValidator });

  onSubmit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.loading.set(true);
    this.errorMessage.set('');

    const { email, password } = this.form.value;

    this.authService.register(email!, password!).subscribe({
      next: () => {
        this.loading.set(false);
        this.router.navigate([this.routePaths.login], {
          queryParams: { registered: 'true' },
        });
      },
      error: (err: HttpErrorResponse) => {
        this.loading.set(false);
        if (err.status === 400 && err.error?.errors) {
          const messages = Object.values(err.error.errors).flat();
          this.errorMessage.set(messages.join(' '));
        } else if (err.error?.detail) {
          this.errorMessage.set(err.error.detail);
        } else {
          this.errorMessage.set('Registration failed. Please try again.');
        }
      },
    });
  }
}

export default SparkRegisterComponent;
