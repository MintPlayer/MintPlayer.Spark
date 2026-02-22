import { Component, inject } from '@angular/core';
import { RouterLink, Router } from '@angular/router';
import { SparkAuthService } from '../../services/spark-auth.service';
import { SPARK_AUTH_CONFIG } from '../../models';

@Component({
  selector: 'spark-auth-bar',
  standalone: true,
  imports: [RouterLink],
  template: `
    @if (authService.isAuthenticated()) {
      <span class="me-2">{{ authService.user()?.userName }}</span>
      <button class="btn btn-outline-light btn-sm" (click)="onLogout()">Logout</button>
    } @else {
      <a class="btn btn-outline-light btn-sm" [routerLink]="config.loginUrl">Login</a>
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
