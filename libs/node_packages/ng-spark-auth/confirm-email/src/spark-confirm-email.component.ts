import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { SparkAuthService } from '@mintplayer/ng-spark-auth/core';
import { SPARK_AUTH_CONFIG, SPARK_AUTH_ROUTE_PATHS } from '@mintplayer/ng-spark-auth/models';
import { TranslateKeyPipe } from '@mintplayer/ng-spark-auth/pipes';

type ConfirmState = 'working' | 'confirmed' | 'changed' | 'invalid';

/**
 * The target of every confirmation mail (#460, D16): `?userId=&code=` confirms an address,
 * `&changedEmail=` completes an email change (M5: one route for both). Posts once, on load, to
 * `POST /spark/auth/confirm-email`. Public — the link is opened from a mailbox, often signed out.
 */
@Component({
  selector: 'spark-confirm-email',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, BsAlertComponent, BsCardComponent, BsCardHeaderComponent, BsSpinnerComponent, TranslateKeyPipe],
  template: `
    <div class="d-flex justify-content-center">
      <bs-card style="width: 100%; max-width: 480px;">
        <bs-card-header>
          <h3 class="mb-0 text-center">{{ 'auth.confirmEmailTitle' | t }}</h3>
        </bs-card-header>
        <div class="p-4">
          @switch (state()) {
            @case ('working') {
              <div class="text-center"><bs-spinner /></div>
            }
            @case ('confirmed') {
              <bs-alert [type]="colors.success" class="spark-confirm-success">{{ 'auth.emailConfirmed' | t }}</bs-alert>
            }
            @case ('changed') {
              <bs-alert [type]="colors.success" class="spark-confirm-success">{{ 'auth.emailChanged' | t }}</bs-alert>
            }
            @case ('invalid') {
              <bs-alert [type]="colors.danger" class="spark-confirm-invalid">{{ 'auth.invalidConfirmLink' | t }}</bs-alert>
            }
          }
          @if (state() !== 'working') {
            <div class="mt-3 text-center">
              @if (auth.isAuthenticated()) {
                <a [routerLink]="homeUrl">{{ 'auth.continue' | t }}</a>
              } @else if (routePaths.login) {
                <a [routerLink]="routePaths.login">{{ 'auth.goToLogin' | t }}</a>
              }
            </div>
          }
        </div>
      </bs-card>
    </div>
  `,
})
export class SparkConfirmEmailComponent {
  protected readonly auth = inject(SparkAuthService);
  private readonly route = inject(ActivatedRoute);
  protected readonly routePaths = inject(SPARK_AUTH_ROUTE_PATHS, { optional: true }) ?? {};
  protected readonly homeUrl = inject(SPARK_AUTH_CONFIG).defaultRedirectUrl;

  protected readonly colors = Color;
  readonly state = signal<ConfirmState>('working');

  constructor() {
    void this.confirm();
  }

  private async confirm(): Promise<void> {
    const query = this.route.snapshot.queryParamMap;
    const userId = query.get('userId');
    const code = query.get('code');
    const changedEmail = query.get('changedEmail');
    if (!userId || !code) {
      this.state.set('invalid');
      return;
    }

    const result = await this.auth.confirmEmail(userId, code, changedEmail);
    this.state.set(!result.success ? 'invalid' : changedEmail ? 'changed' : 'confirmed');
  }
}
