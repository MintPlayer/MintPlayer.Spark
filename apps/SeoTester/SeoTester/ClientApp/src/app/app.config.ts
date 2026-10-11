import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideAnimations } from '@angular/platform-browser/animations';
import { provideSparkServiceWorker } from '@mintplayer/ng-spark/pwa';
import { withSparkLanguage, withSparkTimezone } from '@mintplayer/ng-spark/services';
import { provideSparkAuth, withSparkAuth } from '@mintplayer/ng-spark/auth';
import { provideSparkClientOperations } from '@mintplayer/ng-spark/client-operations';
import { provideSparkNotifications } from '@mintplayer/ng-spark/notifications';
import { provideSparkScoreRenderers } from '@mintplayer/ng-spark/score-renderers';
import { provideSparkDashboards, sparkDashboardRenderers } from '@mintplayer/ng-spark/dashboards';
import { seoRenderers } from './seo/renderers';

import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    // The PWA (docs/guide-pwa.md): Spark's update policy, and push clicks navigate in-app (X10).
    provideSparkServiceWorker(),
    provideRouter(routes),
    // withSparkLanguage: server-resolved texts (refusals, page titles) follow the language selector, not the browser.
    provideHttpClient(...withSparkLanguage(), ...withSparkAuth(), ...withSparkTimezone()),
    provideAnimations(),
    // External sign-in only: the sign-in page is /sign-in (there is no /login page; the default sent the auth bar and
    // sign-out to an unknown route, found in M11).
    provideSparkAuth({ loginUrl: '/sign-in' }),
    provideSparkClientOperations(),
    // The bell's recent list and "see all" link (docs/guide-notifications.md).
    provideSparkNotifications(),
    // The score renderers (gauge, bars, trend, delta), the dashboard renderers (stat, meter, table) and SeoTester's own
    // (seo/renderers.ts), by the model's names.
    provideSparkScoreRenderers([...seoRenderers, ...sparkDashboardRenderers]),
    // The operator dashboard and the database statistics (#504) draw as tiles.
    provideSparkDashboards(),
    provideZonelessChangeDetection(),
  ]
};
