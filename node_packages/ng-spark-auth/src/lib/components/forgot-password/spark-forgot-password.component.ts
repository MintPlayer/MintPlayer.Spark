import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { SparkAuthService } from '../../services/spark-auth.service';
import { SPARK_AUTH_ROUTE_PATHS } from '../../models';
import { TranslateKeyPipe } from '../../pipes/translate-key.pipe';
import { SparkAuthTranslationService } from '../../services/spark-auth-translation.service';

@Component({
  selector: 'spark-forgot-password',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, RouterLink, BsFormComponent, BsFormControlDirective, TranslateKeyPipe],
  template: `
    <div class="d-flex justify-content-center">
      <div class="card" style="width: 100%; max-width: 400px;">
        <div class="card-body">
          <h3 class="card-title text-center mb-4">{{ 'authForgotPasswordTitle' | t }}</h3>

          @if (successMessage()) {
            <div class="alert alert-success" role="alert">{{ successMessage() }}</div>
          }

          @if (errorMessage()) {
            <div class="alert alert-danger" role="alert">{{ errorMessage() }}</div>
          }

          @if (!successMessage()) {
            <p class="text-muted mb-3">
              {{ 'authForgotPasswordDescription' | t }}
            </p>

            <bs-form>
              <form [formGroup]="form" (ngSubmit)="onSubmit()">
                <div class="mb-3">
                  <label for="email" class="form-label">{{ 'authEmail' | t }}</label>
                  <input
                    type="email"
                    id="email"
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
                  {{ 'authSendResetLink' | t }}
                </button>
              </form>
            </bs-form>
          }

          <div class="mt-3 text-center">
            <a [routerLink]="routePaths.login">{{ 'authBackToLogin' | t }}</a>
          </div>
        </div>
      </div>
    </div>
  `,
})
export class SparkForgotPasswordComponent {
  private readonly fb = inject(FormBuilder);
  private readonly authService = inject(SparkAuthService);
  private readonly translation = inject(SparkAuthTranslationService);
  readonly routePaths = inject(SPARK_AUTH_ROUTE_PATHS);

  readonly loading = signal(false);
  readonly errorMessage = signal('');
  readonly successMessage = signal('');

  readonly form = this.fb.group({
    email: ['', [Validators.required, Validators.email]],
  });

  async onSubmit(): Promise<void> {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.loading.set(true);
    this.errorMessage.set('');
    this.successMessage.set('');

    const { email } = this.form.value;

    try {
      await this.authService.forgotPassword(email!);
    } catch {
      // Don't reveal whether the email exists
    }
    this.successMessage.set(this.translation.t('authForgotPasswordSuccess'));
    this.loading.set(false);
  }
}

export default SparkForgotPasswordComponent;
