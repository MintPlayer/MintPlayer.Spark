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
    await this.authService.logout();
    this.router.navigateByUrl('/');
  }
}

export default SparkAuthBarComponent;
