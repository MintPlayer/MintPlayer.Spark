import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideAnimations } from '@angular/platform-browser/animations';
import { provideSpark } from '@mintplayer/ng-spark';
import { withServiceWorker } from '@mintplayer/ng-spark/pwa';
import { withSparkLanguage, withSparkTimezone } from '@mintplayer/ng-spark/services';
import { withAuth, withSparkAuth } from '@mintplayer/ng-spark/auth';
import { withClientOperations } from '@mintplayer/ng-spark/client-operations';
import { withNotifications } from '@mintplayer/ng-spark/notifications';
import { withScoreRenderers } from '@mintplayer/ng-spark/score-renderers';
import { withDashboards, sparkDashboardRenderers } from '@mintplayer/ng-spark/dashboards';
import { withBoards } from '@mintplayer/ng-spark/boards';
import { seoRenderers } from './seo/renderers';

import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    // withSparkLanguage: server-resolved texts (refusals, page titles) follow the language selector, not the browser.
    provideHttpClient(...withSparkLanguage(), ...withSparkAuth(), ...withSparkTimezone()),
    provideAnimations(),
    provideSpark(
      // The PWA (docs/guide-pwa.md): Spark's update policy, and push clicks navigate in-app (X10).
      withServiceWorker(),
      // External sign-in only: the sign-in page is /sign-in (there is no /login page; the default sent the auth bar and
      // sign-out to an unknown route, found in M11).
      withAuth({ loginUrl: '/sign-in' }),
      withClientOperations(),
      // The bell's recent list and "see all" link (docs/guide-notifications.md).
      withNotifications(),
      // The score renderers (gauge, bars, trend, delta), the dashboard renderers (stat, meter, table) and SeoTester's own
      // (seo/renderers.ts), by the model's names.
      withScoreRenderers([...seoRenderers, ...sparkDashboardRenderers]),
      // The operator dashboard and the database statistics (#504) draw as tiles.
      withDashboards(),
      // The home page's board (#530) and "Pin to dashboard" on every detail page.
      withBoards(),
    ),
    provideZonelessChangeDetection(),
  ]
};
