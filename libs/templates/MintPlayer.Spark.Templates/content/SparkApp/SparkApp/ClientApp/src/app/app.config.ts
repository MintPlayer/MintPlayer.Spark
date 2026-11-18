import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient, withXsrfConfiguration } from '@angular/common/http';
import { provideAnimations } from '@angular/platform-browser/animations';
import { provideSpark } from '@mintplayer/ng-spark';
import { withServiceWorker } from '@mintplayer/ng-spark/pwa';
import { withLanguageUrls, withSparkLanguage, withSparkTimezone } from '@mintplayer/ng-spark/services';
//#if (HasAuth)
import { withAuth, withSparkAuth } from '@mintplayer/ng-spark/auth';
//#endif
//#if (UseSoftDelete)
import { withSoftDelete } from '@mintplayer/ng-spark/soft-delete';
//#endif
//#if (UseHistory)
import { withHistory } from '@mintplayer/ng-spark/history';
//#endif
//#if (UseDashboards)
import { withScoreRenderers } from '@mintplayer/ng-spark/score-renderers';
import { withDashboards, sparkDashboardRenderers } from '@mintplayer/ng-spark/dashboards';
//#endif
//#if (UseModeration || UseContributions)
import { withAttributeRenderers } from '@mintplayer/ng-spark/renderers';
//#endif
//#if (UseModeration)
import { withModeration, sparkModerationRenderers } from '@mintplayer/ng-spark/moderation';
//#endif
//#if (UseContributions)
import { withContributions, sparkContributionRenderers } from '@mintplayer/ng-spark/contributions';
//#endif
//#if (UseBoards)
import { withBoards } from '@mintplayer/ng-spark/boards';
//#endif
//#if (UseNotifications)
import { withNotifications } from '@mintplayer/ng-spark/notifications';
//#endif
//#if (UseOpenSearchDescription)
import { withOpenSearchDescription, withSearch } from '@mintplayer/ng-spark/search';
//#elseif (UseSearch)
import { withSearch } from '@mintplayer/ng-spark/search';
//#endif

import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
//#if (HasAuth)
    provideHttpClient(...withSparkAuth(), ...withSparkLanguage(), ...withSparkTimezone()),
//#else
    provideHttpClient(withXsrfConfiguration({ cookieName: 'XSRF-TOKEN', headerName: 'X-XSRF-TOKEN' }), ...withSparkLanguage(), ...withSparkTimezone()),
//#endif
    provideAnimations(),
    provideZonelessChangeDetection(),
    provideSpark(
      withLanguageUrls(),
      withServiceWorker(),
//#if (HasAuth)
      withAuth(),
//#endif
//#if (UseSoftDelete)
      withSoftDelete(),
//#endif
//#if (UseHistory)
      withHistory(),
//#endif
//#if (UseDashboards)
      withDashboards(),
      withScoreRenderers([...sparkDashboardRenderers]),
//#endif
//#if (UseModeration)
      withModeration(),
//#endif
//#if (UseContributions)
      withContributions(),
//#endif
//#if (UseBoards)
      withBoards(),
//#endif
//#if (UseNotifications)
      withNotifications(),
//#endif
//#if (UseSearch)
      withSearch(),
//#endif
//#if (UseOpenSearchDescription)
      withOpenSearchDescription({ title: 'SparkApp' }),
//#endif
//#if (UseModeration || UseContributions)
      withAttributeRenderers([
//#if (UseModeration)
        ...sparkModerationRenderers,
//#endif
//#if (UseContributions)
        ...sparkContributionRenderers,
//#endif
      ]),
//#endif
    ),
  ]
};
