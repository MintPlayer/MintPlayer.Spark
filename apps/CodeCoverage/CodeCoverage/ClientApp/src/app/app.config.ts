import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideAnimations } from '@angular/platform-browser/animations';
import { provideSparkAuth, withSparkAuth } from '@mintplayer/ng-spark-auth';
import { provideSparkAttributeRenderers } from '@mintplayer/ng-spark/renderers';
import { provideSparkClientOperations } from '@mintplayer/ng-spark/client-operations';
import { sparkLanguageInterceptor } from './spark/spark-language.interceptor';

import { routes } from './app.routes';
import { CoverageBarRendererComponent } from './spark/coverage-bar-renderer.component';
import { CoverageSummaryDetailRendererComponent } from './spark/coverage-summary-detail-renderer.component';
import { CoverageSparklineRendererComponent } from './spark/coverage-sparkline-renderer.component';
import { ShortShaRendererComponent } from './spark/short-sha-renderer.component';
import { BuildSessionsRendererComponent } from './spark/build-sessions-renderer.component';
import { RepoNameRendererComponent } from './spark/repo-name-renderer.component';
import { DateTimeRendererComponent } from './spark/date-time-renderer.component';
import { CoverageDeltaRendererComponent } from './spark/coverage-delta-renderer.component';
import { AccountAvatarRendererComponent } from './spark/account-avatar-renderer.component';
import { AccountLinkRendererComponent } from './spark/account-link-renderer.component';
import { AccountCoverageRendererComponent } from './spark/account-coverage-renderer.component';
import { AppInstalledRendererComponent } from './spark/app-installed-renderer.component';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(withInterceptors([sparkLanguageInterceptor]), ...withSparkAuth()),
    provideAnimations(),
    provideSparkAuth(),
    provideSparkAttributeRenderers([
      {
        name: 'coverage-bar',
        detailComponent: CoverageSummaryDetailRendererComponent,
        columnComponent: CoverageBarRendererComponent,
      },
      {
        name: 'coverage-sparkline',
        detailComponent: CoverageSparklineRendererComponent,
        columnComponent: CoverageSparklineRendererComponent,
      },
      {
        name: 'short-sha',
        detailComponent: ShortShaRendererComponent,
        columnComponent: ShortShaRendererComponent,
      },
      {
        name: 'build-sessions',
        detailComponent: BuildSessionsRendererComponent,
        columnComponent: BuildSessionsRendererComponent,
      },
      {
        name: 'repo-name',
        detailComponent: RepoNameRendererComponent,
        columnComponent: RepoNameRendererComponent,
      },
      {
        name: 'date-time',
        detailComponent: DateTimeRendererComponent,
        columnComponent: DateTimeRendererComponent,
      },
      {
        name: 'coverage-delta',
        detailComponent: CoverageDeltaRendererComponent,
        columnComponent: CoverageDeltaRendererComponent,
      },
      // The composed Home page's accounts grid. Column-only: MyAccountRow is a
      // virtual type with Query but no Read, so its rows have no detail page for
      // a detail renderer to run on.
      {
        name: 'account-avatar',
        columnComponent: AccountAvatarRendererComponent,
      },
      {
        name: 'account-link',
        columnComponent: AccountLinkRendererComponent,
      },
      {
        name: 'account-coverage',
        columnComponent: AccountCoverageRendererComponent,
      },
      {
        name: 'app-installed',
        columnComponent: AppInstalledRendererComponent,
      },
    ]),
    // The Resync custom action's refreshQuery operation is dispatched here; without
    // this provider the action runs and the grid silently keeps its stale rows.
    provideSparkClientOperations(),
    provideZonelessChangeDetection()
  ]
};
