import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink, Router } from '@angular/router';
import { SparkAuthService } from '../../services/spark-auth.service';
import { SPARK_AUTH_CONFIG } from '../../models';
import { TranslateKeyPipe } from '../../pipes/translate-key.pipe';

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
