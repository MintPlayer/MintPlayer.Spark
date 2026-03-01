import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { RouterLink, ActivatedRoute, Router } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { SparkAuthService } from '../../services/spark-auth.service';
import { SPARK_AUTH_CONFIG, SPARK_AUTH_ROUTE_PATHS } from '../../models';
import { TranslateKeyPipe } from '../../pipes/translate-key.pipe';
import { SparkAuthTranslationService } from '../../services/spark-auth-translation.service';

@Component({
  selector: 'spark-two-factor',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, RouterLink, BsAlertComponent, BsCardComponent, BsCardHeaderComponent, BsFormComponent, BsFormControlDirective, TranslateKeyPipe],
  templateUrl: './spark-two-factor.component.html',
})
export class SparkTwoFactorComponent {
  private readonly fb = inject(FormBuilder);
  private readonly authService = inject(SparkAuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly config = inject(SPARK_AUTH_CONFIG);
  private readonly translation = inject(SparkAuthTranslationService);
  readonly routePaths = inject(SPARK_AUTH_ROUTE_PATHS);

  colors = Color;
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

  async onSubmit(): Promise<void> {
    const isRecovery = this.useRecoveryCode();
    const code = isRecovery ? this.form.value.recoveryCode : this.form.value.code;

    if (!code?.trim()) {
      this.errorMessage.set(isRecovery
        ? this.translation.t('authEnterRecoveryCodeError')
        : this.translation.t('authEnterCodeError'));
      return;
    }

    this.loading.set(true);
    this.errorMessage.set('');

    const twoFactorCode = isRecovery ? undefined : code;
    const twoFactorRecoveryCode = isRecovery ? code : undefined;

    try {
      await this.authService.loginTwoFactor(twoFactorCode ?? '', twoFactorRecoveryCode);
      const returnUrl = this.route.snapshot.queryParamMap.get('returnUrl');
      this.router.navigateByUrl(returnUrl || this.config.defaultRedirectUrl);
    } catch {
      this.errorMessage.set(this.translation.t('authInvalidCode'));
    } finally {
      this.loading.set(false);
    }
  }
}

export default SparkTwoFactorComponent;
