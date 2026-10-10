import type { SparkAttributeRendererRegistration } from '@mintplayer/ng-spark/renderers';
import { CoverageBarRendererComponent } from './coverage-bar-renderer.component';
import { CoverageSummaryDetailRendererComponent } from './coverage-summary-detail-renderer.component';
import { CoverageSparklineRendererComponent } from './coverage-sparkline-renderer.component';
import { ShortShaRendererComponent } from './short-sha-renderer.component';
import { BuildSessionsRendererComponent } from './build-sessions-renderer.component';
import { RepoNameRendererComponent } from './repo-name-renderer.component';
import { PrivateLockRendererComponent } from './private-lock-renderer.component';
import { DateTimeRendererComponent } from './date-time-renderer.component';
import { CoverageDeltaRendererComponent } from './coverage-delta-renderer.component';
import { AccountAvatarRendererComponent } from './account-avatar-renderer.component';
import { AccountLinkRendererComponent } from './account-link-renderer.component';
import { AccountCoverageRendererComponent } from './account-coverage-renderer.component';
import { ConnectedRendererComponent } from './connected-renderer.component';

/**
 * The attribute renderers this app registers, by the `renderer` names its App_Data model uses.
 * A list of its own rather than inline in app.config.ts, so a spec can render exactly what
 * production resolves for a name (score-renderers.characterisation.spec.ts).
 */
export const codeCoverageRenderers: SparkAttributeRendererRegistration[] = [
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
    name: 'private-lock',
    detailComponent: PrivateLockRendererComponent,
    columnComponent: PrivateLockRendererComponent,
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
    name: 'connected',
    columnComponent: ConnectedRendererComponent,
  },
];
