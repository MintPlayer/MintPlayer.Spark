import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { SparkAuthService } from '../../services/spark-auth.service';
import { SPARK_AUTH_ROUTE_PATHS } from '../../models';
import { TranslateKeyPipe } from '../../pipes/translate-key.pipe';
import { SparkAuthTranslationService } from '../../services/spark-auth-translation.service';

@Component({
  selector: 'spark-forgot-password',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, RouterLink, BsAlertComponent, BsCardComponent, BsCardHeaderComponent, BsFormComponent, BsFormControlDirective, BsSpinnerComponent, TranslateKeyPipe],
  templateUrl: './spark-forgot-password.component.html',
})
export class SparkForgotPasswordComponent {
  private readonly fb = inject(FormBuilder);
  private readonly authService = inject(SparkAuthService);
  private readonly translation = inject(SparkAuthTranslationService);
  readonly routePaths = inject(SPARK_AUTH_ROUTE_PATHS);

  colors = Color;
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
