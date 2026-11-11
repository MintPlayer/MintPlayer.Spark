import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideAnimations } from '@angular/platform-browser/animations';
import { provideSpark } from '@mintplayer/ng-spark';
import { withServiceWorker } from '@mintplayer/ng-spark/pwa';
import { withLanguageUrls, withSparkLanguage, withSparkTimezone } from '@mintplayer/ng-spark/services';
import { withAuth, withSparkAuth } from '@mintplayer/ng-spark/auth';
import { withAttributeRenderers } from '@mintplayer/ng-spark/renderers';
import { withClientOperations } from '@mintplayer/ng-spark/client-operations';
import { withSoftDelete } from '@mintplayer/ng-spark/soft-delete';
import { withHistory } from '@mintplayer/ng-spark/history';
import { withModeration, sparkModerationRenderers } from '@mintplayer/ng-spark/moderation';
import { withContributions, sparkContributionRenderers } from '@mintplayer/ng-spark/contributions';

import { routes } from './app.routes';
import { AuthorRendererComponent } from './renderers/author-renderer.component';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    // withSparkTimezone() (#460 M9): the browser's zone travels as a header on every Spark call and is
    // kept in the spark-timezone cookie, so a server-side render could read it too.
    provideHttpClient(...withSparkAuth(), ...withSparkLanguage(), ...withSparkTimezone()),
    provideAnimations(),
    provideZonelessChangeDetection(),
    provideSpark(
      withLanguageUrls(),
      withServiceWorker(),
      withAuth(),
      withClientOperations(),
      // #460 M6: the Deleted toggle on query lists, Restore / Purge on a deleted row.
      withSoftDelete(),
      // #460 M7: the History card (revisions, diff, Revert) under every detail page.
      withHistory(),
      // #460 M12: Flag on every detail page, the moderator panel, the review-queue link.
      withModeration(),
      // Contributions M6: "Revert to this version" in the translation history (row menu and detail page).
      withContributions(),
      // The vote widget (`spark-vote`, M12), the translation attribution line and line diff
      // (Contributions), and QnA's author cell (reputation badge).
      withAttributeRenderers([
        ...sparkModerationRenderers,
        ...sparkContributionRenderers,
        { name: 'qna-author', detailComponent: AuthorRendererComponent, columnComponent: AuthorRendererComponent },
      ]),
    ),
  ]
};
