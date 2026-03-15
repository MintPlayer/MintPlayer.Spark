import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { RouterLink, ActivatedRoute, Router } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsToggleButtonComponent } from '@mintplayer/ng-bootstrap/toggle-button';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { SparkAuthService } from '../../services/spark-auth.service';
import { SPARK_AUTH_CONFIG, SPARK_AUTH_ROUTE_PATHS, SPARK_OIDC_PROVIDERS, SparkOidcProvider } from '../../models';
import { TranslateKeyPipe } from '../../pipes/translate-key.pipe';
import { SparkAuthTranslationService } from '../../services/spark-auth-translation.service';

@Component({
  selector: 'spark-login',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, RouterLink, BsAlertComponent, BsCardComponent, BsCardHeaderComponent, BsFormComponent, BsFormControlDirective, BsToggleButtonComponent, BsSpinnerComponent, TranslateKeyPipe],
  templateUrl: './spark-login.component.html',
})
export class SparkLoginComponent {
  private readonly fb = inject(FormBuilder);
  private readonly authService = inject(SparkAuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly config = inject(SPARK_AUTH_CONFIG);
  private readonly translation = inject(SparkAuthTranslationService);
  private readonly oidcProviders = inject(SPARK_OIDC_PROVIDERS, { optional: true });
  readonly routePaths = inject(SPARK_AUTH_ROUTE_PATHS);

  colors = Color;
  readonly loading = signal(false);
  readonly errorMessage = signal('');
  readonly externalProviders = computed(() => this.oidcProviders ?? []);

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

  openExternalLogin(provider: SparkOidcProvider): void {
    const returnUrl = this.route.snapshot.queryParamMap.get('returnUrl') || this.config.defaultRedirectUrl;
    const url = `${this.config.apiBasePath}/external-login/${provider.scheme}?returnUrl=${encodeURIComponent(returnUrl)}&popup=true`;

    const popup = window.open(url, '_blank', 'width=600,height=600');

    const listener = (event: MessageEvent) => {
      if (event.origin !== window.location.origin) return;

      let data: { status: string; scheme?: string; error?: string; errorDescription?: string };
      try {
        data = typeof event.data === 'string' ? JSON.parse(event.data) : event.data;
      } catch {
        return;
      }

      if (!data?.status) return;

      window.removeEventListener('message', listener);

      if (data.status === 'success') {
        this.authService.csrfRefresh().then(() =>
          this.authService.checkAuth()
        ).then(() => {
          this.router.navigateByUrl(returnUrl);
        });
      } else {
        this.errorMessage.set(data.errorDescription || data.error || 'External login failed');
      }
    };

    window.addEventListener('message', listener);

    // Clean up listener if user closes popup manually
    const timer = setInterval(() => {
      if (popup?.closed) {
        clearInterval(timer);
        window.removeEventListener('message', listener);
      }
    }, 500);
  }

  getProviderButtonClass(provider: SparkOidcProvider): string {
    return `btn btn-outline-${provider.buttonClass ?? 'secondary'} w-100 mb-2`;
  }

  getProviderIconClass(provider: SparkOidcProvider): string {
    return `bi bi-${provider.icon} me-2`;
  }
}

export default SparkLoginComponent;
