import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { SparkAuthService } from '@mintplayer/ng-spark-auth/core';
import { SPARK_AUTH_ROUTE_PATHS, SparkAuthRoutePaths } from '@mintplayer/ng-spark-auth/models';
import { TranslateKeyPipe } from '@mintplayer/ng-spark-auth/pipes';

/** The account pages this app mounted, in display order, with their title keys. */
const PAGES: { key: keyof SparkAuthRoutePaths; label: string }[] = [
  { key: 'profile', label: 'auth.profileTitle' },
  { key: 'changePassword', label: 'auth.changePasswordTitle' },
  { key: 'twoFactorSetup', label: 'auth.twoFactorSetupTitle' },
  { key: 'externalLogins', label: 'auth.externalLoginsTitle' },
  { key: 'passkeys', label: 'auth.passkeysTitle' },
  { key: 'personalData', label: 'auth.personalDataTitle' },
];

/** The account area's landing page (#460, D16): a link per mounted account page. */
@Component({
  selector: 'spark-account-overview',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, BsCardComponent, BsCardHeaderComponent, TranslateKeyPipe],
  template: `
    <div class="d-flex justify-content-center">
      <bs-card style="width: 100%; max-width: 640px;">
        <bs-card-header>
          <h3 class="mb-0">{{ 'auth.accountTitle' | t }}</h3>
          @if (auth.user()?.userName; as name) { <small class="text-muted">{{ name }}</small> }
        </bs-card-header>
        <div class="p-4">
          <ul class="list-unstyled mb-0">
            @for (page of pages; track page.key) {
              <li class="py-1"><a class="spark-account-link" [routerLink]="page.path">{{ page.label | t }}</a></li>
            }
          </ul>
        </div>
      </bs-card>
    </div>
  `,
})
export class SparkAccountOverviewComponent {
  protected readonly auth = inject(SparkAuthService);
  private readonly paths = inject(SPARK_AUTH_ROUTE_PATHS, { optional: true }) ?? {};

  protected readonly pages = PAGES
    .filter(page => !!this.paths[page.key])
    .map(page => ({ ...page, path: this.paths[page.key]! }));
}
