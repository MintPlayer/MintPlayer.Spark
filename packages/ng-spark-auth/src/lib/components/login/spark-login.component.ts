import { Component, inject, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { RouterLink, ActivatedRoute, Router } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { SparkAuthService } from '../../services/spark-auth.service';
import { SPARK_AUTH_CONFIG, SPARK_AUTH_ROUTE_PATHS } from '../../models';

@Component({
  selector: 'spark-login',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink],
  template: `
    <div class="d-flex justify-content-center">
      <div class="card" style="width: 100%; max-width: 400px;">
        <div class="card-body">
          <h3 class="card-title text-center mb-4">Login</h3>

          @if (errorMessage()) {
            <div class="alert alert-danger" role="alert">{{ errorMessage() }}</div>
          }

          <form [formGroup]="form" (ngSubmit)="onSubmit()">
            <div class="mb-3">
              <label for="email" class="form-label">Email</label>
              <input
                type="text"
                id="email"
                class="form-control"
                formControlName="email"
                autocomplete="username"
              />
            </div>

            <div class="mb-3">
              <label for="password" class="form-label">Password</label>
              <input
                type="password"
                id="password"
                class="form-control"
                formControlName="password"
                autocomplete="current-password"
              />
            </div>

            <div class="mb-3 form-check">
              <input
                type="checkbox"
                id="rememberMe"
                class="form-check-input"
                formControlName="rememberMe"
              />
              <label for="rememberMe" class="form-check-label">Remember me</label>
            </div>

            <button
              type="submit"
              class="btn btn-primary w-100"
              [disabled]="loading()"
            >
              @if (loading()) {
                <span class="spinner-border spinner-border-sm me-1" role="status"></span>
              }
              Login
            </button>
          </form>

          <div class="mt-3 text-center">
            <a [routerLink]="routePaths.register">Create an account</a>
          </div>
          <div class="mt-2 text-center">
            <a [routerLink]="routePaths.forgotPassword">Forgot password?</a>
          </div>
        </div>
      </div>
    </div>
  `,
})
export class SparkLoginComponent {
  private readonly fb = inject(FormBuilder);
  private readonly authService = inject(SparkAuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly config = inject(SPARK_AUTH_CONFIG);
  readonly routePaths = inject(SPARK_AUTH_ROUTE_PATHS);

  readonly loading = signal(false);
  readonly errorMessage = signal('');

  readonly form = this.fb.group({
    email: ['', Validators.required],
    password: ['', Validators.required],
    rememberMe: [false],
  });

  onSubmit(): void {
    if (this.form.invalid) return;

    this.loading.set(true);
    this.errorMessage.set('');

    const { email, password } = this.form.value;

    this.authService.login(email!, password!).subscribe({
      next: () => {
        this.loading.set(false);
        const returnUrl = this.route.snapshot.queryParamMap.get('returnUrl');
        this.router.navigateByUrl(returnUrl || this.config.defaultRedirectUrl);
      },
      error: (err: HttpErrorResponse) => {
        this.loading.set(false);
        if (err.status === 401 && err.error?.detail === 'RequiresTwoFactor') {
          const returnUrl = this.route.snapshot.queryParamMap.get('returnUrl');
          this.router.navigate([this.routePaths.twoFactor], {
            queryParams: returnUrl ? { returnUrl } : undefined,
          });
        } else {
          this.errorMessage.set('Invalid email or password.');
        }
      },
    });
  }
}

export default SparkLoginComponent;
