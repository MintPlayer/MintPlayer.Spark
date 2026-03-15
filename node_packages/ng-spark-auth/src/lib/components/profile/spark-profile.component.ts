import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { SparkAuthService } from '../../services/spark-auth.service';
import { SPARK_AUTH_CONFIG, SPARK_OIDC_PROVIDERS, SparkOidcProvider } from '../../models';
import { ExternalLogin } from '../../models/external-login';
import { TranslateKeyPipe } from '../../pipes/translate-key.pipe';

@Component({
  selector: 'spark-profile',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BsAlertComponent, BsCardComponent, BsCardHeaderComponent, BsTableComponent, TranslateKeyPipe],
  templateUrl: './spark-profile.component.html',
})
export class SparkProfileComponent {
  readonly authService = inject(SparkAuthService);
  private readonly router = inject(Router);
  private readonly config = inject(SPARK_AUTH_CONFIG);
  private readonly oidcProviders = inject(SPARK_OIDC_PROVIDERS, { optional: true });

  colors = Color;
  readonly logins = signal<ExternalLogin[]>([]);
  readonly errorMessage = signal('');
  readonly successMessage = signal('');

  readonly allProviders = computed(() => this.oidcProviders ?? []);

  readonly availableProviders = computed(() => {
    const linked = new Set(this.logins().map(l => l.loginProvider));
    return this.allProviders().filter(p => !linked.has(p.scheme));
  });

  constructor() {
    this.loadLogins();
  }

  private async loadLogins(): Promise<void> {
    try {
      const logins = await this.authService.getExternalLogins();
      this.logins.set(logins);
    } catch {
      this.errorMessage.set('Failed to load external logins.');
    }
  }

  async removeLogin(login: ExternalLogin): Promise<void> {
    this.errorMessage.set('');
    this.successMessage.set('');

    try {
      await this.authService.removeExternalLogin(login.loginProvider);
      this.logins.update(prev => prev.filter(l => l.loginProvider !== login.loginProvider));
      this.successMessage.set(`Removed ${login.providerDisplayName || login.loginProvider} login.`);
    } catch {
      this.errorMessage.set(`Failed to remove ${login.providerDisplayName || login.loginProvider} login.`);
    }
  }

  addLogin(provider: SparkOidcProvider): void {
    this.errorMessage.set('');
    this.successMessage.set('');

    const returnUrl = this.router.url;
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
        this.successMessage.set(`Added ${provider.displayName} login.`);
        this.loadLogins();
      } else {
        this.errorMessage.set(data.errorDescription || data.error || 'Failed to add external login.');
      }
    };

    window.addEventListener('message', listener);

    const timer = setInterval(() => {
      if (popup?.closed) {
        clearInterval(timer);
        window.removeEventListener('message', listener);
      }
    }, 500);
  }
}

export default SparkProfileComponent;
