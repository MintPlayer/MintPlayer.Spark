import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideAnimations } from '@angular/platform-browser/animations';
import { withSparkTimezone } from '@mintplayer/ng-spark/services';
import { provideSparkAuth, withSparkAuth } from '@mintplayer/ng-spark-auth';
import { provideSparkAttributeRenderers } from '@mintplayer/ng-spark/renderers';
import { provideSparkClientOperations } from '@mintplayer/ng-spark/client-operations';
import { provideSparkSoftDelete } from '@mintplayer/ng-spark/soft-delete';
import { provideSparkHistory } from '@mintplayer/ng-spark/history';
import { provideSparkModeration, sparkModerationRenderers } from '@mintplayer/ng-spark/moderation';
import { provideSparkContributions, sparkContributionRenderers } from '@mintplayer/ng-spark/contributions';

import { routes } from './app.routes';
import { AuthorRendererComponent } from './renderers/author-renderer.component';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    // withSparkTimezone() (#460 M9): the browser's zone travels as a header on every Spark call and is
    // kept in the spark-timezone cookie, so a server-side render could read it too.
    provideHttpClient(...withSparkAuth(), ...withSparkTimezone()),
    provideAnimations(),
    provideSparkAuth(),
    provideSparkClientOperations(),
    provideZonelessChangeDetection(),
    // #460 M6: the Deleted toggle on query lists, Restore / Purge on a deleted row.
    provideSparkSoftDelete(),
    // #460 M7: the History card (revisions, diff, Revert) under every detail page.
    provideSparkHistory(),
    // #460 M12: Flag on every detail page, the moderator panel, the review-queue link.
    provideSparkModeration(),
    // Contributions M6: "Revert to this version" in the translation history (row menu and detail page).
    provideSparkContributions(),
    // One list: the vote widget (`spark-vote`, M12), the translation attribution line and line diff
    // (Contributions), and QnA's author cell (reputation badge).
    provideSparkAttributeRenderers([
      ...sparkModerationRenderers,
      ...sparkContributionRenderers,
      { name: 'qna-author', detailComponent: AuthorRendererComponent, columnComponent: AuthorRendererComponent },
    ]),
  ]
};
