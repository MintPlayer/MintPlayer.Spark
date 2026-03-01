import { Component, inject, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { RouterLink, ActivatedRoute, Router } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsToggleButtonComponent } from '@mintplayer/ng-bootstrap/toggle-button';
import { SparkAuthService } from '../../services/spark-auth.service';
import { SPARK_AUTH_CONFIG, SPARK_AUTH_ROUTE_PATHS } from '../../models';
import { TranslateKeyPipe } from '../../pipes/translate-key.pipe';
import { SparkAuthTranslationService } from '../../services/spark-auth-translation.service';

@Component({
  selector: 'spark-login',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, BsFormComponent, BsFormControlDirective, BsToggleButtonComponent, TranslateKeyPipe],
  template: `
    <div class="d-flex justify-content-center">
      <div class="card" style="width: 100%; max-width: 400px;">
        <div class="card-body">
          <h3 class="card-title text-center mb-4">{{ 'authLogin' | t }}</h3>

          @if (errorMessage()) {
            <div class="alert alert-danger" role="alert">{{ errorMessage() }}</div>
          }

          <bs-form>
            <form [formGroup]="form" (ngSubmit)="onSubmit()">
              <div class="mb-3">
                <label for="email" class="form-label">{{ 'authEmail' | t }}</label>
                <input
                  type="text"
                  id="email"
                  formControlName="email"
                  autocomplete="username"
                />
              </div>

              <div class="mb-3">
                <label for="password" class="form-label">{{ 'authPassword' | t }}</label>
                <input
                  type="password"
                  id="password"
                  formControlName="password"
                  autocomplete="current-password"
                />
              </div>

              <div class="mb-3">
                <bs-toggle-button [type]="'checkbox'" formControlName="rememberMe" [name]="'rememberMe'">{{ 'authRememberMe' | t }}</bs-toggle-button>
              </div>

              <button
                type="submit"
                class="btn btn-primary w-100"
                [disabled]="loading()"
              >
                @if (loading()) {
                  <span class="spinner-border spinner-border-sm me-1" role="status"></span>
                }
                {{ 'authLogin' | t }}
              </button>
            </form>
          </bs-form>

          <div class="mt-3 text-center">
            <a [routerLink]="routePaths.register">{{ 'authCreateAccount' | t }}</a>
          </div>
          <div class="mt-2 text-center">
            <a [routerLink]="routePaths.forgotPassword">{{ 'authForgotPassword' | t }}</a>
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
  private readonly translation = inject(SparkAuthTranslationService);
  readonly routePaths = inject(SPARK_AUTH_ROUTE_PATHS);

  readonly loading = signal(false);
  readonly errorMessage = signal('');

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
      const returnUrl = this.route.snapshot.queryParamMap.get('returnUrl');
      this.router.navigateByUrl(returnUrl || this.config.defaultRedirectUrl);
    } catch (err: any) {
      if (err instanceof HttpErrorResponse && err.status === 401 && err.error?.detail === 'RequiresTwoFactor') {
        const returnUrl = this.route.snapshot.queryParamMap.get('returnUrl');
        this.router.navigate([this.routePaths.twoFactor], {
          queryParams: returnUrl ? { returnUrl } : undefined,
        });
      } else {
        this.errorMessage.set(this.translation.t('authInvalidCredentials'));
      }
    } finally {
      this.loading.set(false);
    }
  }
}

export default SparkLoginComponent;
