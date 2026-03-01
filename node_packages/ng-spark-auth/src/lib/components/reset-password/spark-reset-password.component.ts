import { ChangeDetectionStrategy, Component, inject, OnInit, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators, AbstractControl, ValidationErrors } from '@angular/forms';
import { RouterLink, ActivatedRoute, Router } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
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
  imports: [ReactiveFormsModule, RouterLink, BsAlertComponent, BsCardComponent, BsCardHeaderComponent, BsFormComponent, BsFormControlDirective, BsSpinnerComponent, TranslateKeyPipe],
  templateUrl: './spark-reset-password.component.html',
})
export class SparkResetPasswordComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly authService = inject(SparkAuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly translation = inject(SparkAuthTranslationService);
  readonly routePaths = inject(SPARK_AUTH_ROUTE_PATHS);

  colors = Color;
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
