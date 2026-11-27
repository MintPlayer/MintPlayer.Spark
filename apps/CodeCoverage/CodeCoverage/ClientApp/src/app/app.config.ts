import { ApplicationConfig, inject, provideAppInitializer, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideAnimations } from '@angular/platform-browser/animations';
import { provideSpark } from '@mintplayer/ng-spark';
import { withServiceWorker } from '@mintplayer/ng-spark/pwa';
import { withAuth, withSparkAuth } from '@mintplayer/ng-spark/auth';
import { withScoreRenderers } from '@mintplayer/ng-spark/score-renderers';
import { withDashboards, sparkDashboardRenderers } from '@mintplayer/ng-spark/dashboards';
import { withBoards } from '@mintplayer/ng-spark/boards';
import { withClientOperations } from '@mintplayer/ng-spark/client-operations';
import { withFileAttributes } from '@mintplayer/ng-spark/files';
import { withNotifications } from '@mintplayer/ng-spark/notifications';
import { withLanguageUrls, withSparkLanguage, withSparkTimezone } from '@mintplayer/ng-spark/services';

import { routes } from './app.routes';
import { HOME_URL } from './spark/home-route';
import { codeCoverageRenderers } from './spark/renderers';
import { RulesetNoticesService } from './services/ruleset-notices.service';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(...withSparkLanguage(), ...withSparkAuth(), ...withSparkTimezone()),
    provideAnimations(),
    provideSpark(
      withLanguageUrls(),
      withServiceWorker(),
      // ⚠️ Not the default. loginUrl defaults to '/login', and this app mounts '/sign-in'
      // (sparkAuthRoutes(withExternalLogin(...)) in app.routes.ts) — so the route guard, the 401
      // interceptor and the auth bar were all redirecting to a page that does not exist. The
      // symptom was a blank shell rather than an error, which is why it survived.
      withAuth({ loginUrl: '/sign-in', defaultRedirectUrl: HOME_URL }),
      // The app's renderers first (they win a name clash), then the dashboard renderers (stat, meter, table) and the
      // score renderers (spark-trend draws the host metrics dashboard's history).
      withScoreRenderers([...codeCoverageRenderers, ...sparkDashboardRenderers]),
      // The host metrics dashboard and the package's database statistics (#504, M5b) draw as tiles.
      withDashboards(),
      // Home's "home" board (#530, rendered by HomeExtrasComponent) and "Pin to dashboard" on detail pages.
      withBoards(),
      // The Resync custom action's refreshQuery operation is dispatched here; without
      // this feature the action runs and the grid silently keeps its stale rows.
      withClientOperations(),
      // #657 M8: the bell's stored notifications (a format run's outcome) and its "see all" link (docs/guide-notifications.md).
      withNotifications(),
      // #700 M11: the Image renderers (the account avatar, CustomAvatar's upload).
      withFileAttributes(),
    ),
    // #714 Dec5, Dec11: the merge-queue ruleset notices reach the bell whenever the app becomes signed in.
    provideAppInitializer(() => inject(RulesetNoticesService).start()),
    provideZonelessChangeDetection()
  ]
};
