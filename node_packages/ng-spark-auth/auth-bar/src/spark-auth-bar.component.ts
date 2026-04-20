import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink, Router } from '@angular/router';
import { SPARK_AUTH_CONFIG } from '@mintplayer/ng-spark-auth/models';
import { SparkAuthService } from '@mintplayer/ng-spark-auth/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark-auth/pipes';

@Component({
  selector: 'spark-auth-bar',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, TranslateKeyPipe],
  templateUrl: './spark-auth-bar.component.html',
})
export class SparkAuthBarComponent {
  readonly authService = inject(SparkAuthService);
  readonly config = inject(SPARK_AUTH_CONFIG);
  private readonly router = inject(Router);

  async onLogout(): Promise<void> {
    try {
      await this.authService.logout();
    } finally {
      // Always navigate away from the authenticated area, even if the server-side
      // logout call fails (network error, session already expired, etc.). The local
      // session state has been cleared by SparkAuthService regardless.
      this.router.navigateByUrl('/');
    }
  }
}

export default SparkAuthBarComponent;
