import { Component, inject } from '@angular/core';
import { RouterLink, Router } from '@angular/router';
import { SparkAuthService } from '../../services/spark-auth.service';
import { SPARK_AUTH_CONFIG } from '../../models';
import { TranslateKeyPipe } from '../../pipes/translate-key.pipe';

@Component({
  selector: 'spark-auth-bar',
  standalone: true,
  imports: [RouterLink, TranslateKeyPipe],
  template: `
    @if (authService.isAuthenticated()) {
      <span class="me-2">{{ authService.user()?.userName }}</span>
      <button class="btn btn-outline-light btn-sm" (click)="onLogout()">{{ 'authLogout' | t }}</button>
    } @else {
      <a class="btn btn-outline-light btn-sm" [routerLink]="config.loginUrl">{{ 'authLogin' | t }}</a>
    }
  `,
})
export class SparkAuthBarComponent {
  readonly authService = inject(SparkAuthService);
  readonly config = inject(SPARK_AUTH_CONFIG);
  private readonly router = inject(Router);

  onLogout(): void {
    this.authService.logout().subscribe(() => {
      this.router.navigateByUrl('/');
    });
  }
}

export default SparkAuthBarComponent;
