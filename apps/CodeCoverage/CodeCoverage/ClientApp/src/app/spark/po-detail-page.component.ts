import { ChangeDetectionStrategy, Component } from '@angular/core';
import { SparkPoDetailComponent } from '@mintplayer/ng-spark/po-detail';
import type { PersistentObject } from '@mintplayer/ng-spark/models';
import { valueFor } from '@mintplayer/ng-spark/models';
import { RepoBadgePanelComponent } from '../components/repo-badge-panel/repo-badge-panel.component';
import { RepoTrendPanelComponent } from '../components/repo-trend-panel/repo-trend-panel.component';
import { RepoSetupPanelComponent } from '../components/repo-setup-panel/repo-setup-panel.component';
import { RepoCommitGraphPanelComponent } from '../components/repo-commit-graph-panel/repo-commit-graph-panel.component';
import { RepoMergeQueuePanelComponent } from '../components/repo-merge-queue-panel/repo-merge-queue-panel.component';
import { AccountDependencyGraphPanelComponent } from '../components/account-dependency-graph-panel/account-dependency-graph-panel.component';
import { AccountPermissionsNoticeComponent } from '../components/account-permissions-notice/account-permissions-notice.component';
import { CommitFilesExtrasComponent } from './commit-files-extras.component';
import { forgeOf } from './forge-of';
import { HomeExtrasComponent } from './home-extras.component';

/**
 * The app's poDetail route component (`sparkRoutes({ poDetail })` override).
 *
 * Every entity type renders the stock generic detail page — the action bar, the `<h2>` Spark
 * draws from the object's breadcrumb, the attribute cards and the declared sub-queries — plus
 * the app-specific panels that cannot be expressed as attribute renderers, mounted through
 * `extraContentTemplate`.
 *
 * Extras render **last**, after the attributes and after every query card. That is what makes
 * this work as a page: on Account the order comes out title → attributes → repositories grid →
 * upload tokens, which is the page a reader expects.
 *
 * The one exception is the Account's pending-permissions notice, mounted through
 * `beforeQueriesTemplate`: it renders after the attributes and BEFORE the repositories grid,
 * because a notice the owner has to act on is no use below a long grid.
 *
 * ⚠️ It no longer forwards anything. Until preview.67 this component pre-fetched the whole
 * PersistentObject on every navigation just to decide whether Account should redirect to a
 * hand-written `/{provider}/a/{login}` page — a wasted round-trip on every detail view of every type.
 * The vanity URLs now point the other way, as guards in `vanity-redirects.ts`:
 * `/{provider}/a/{login}`, `/{provider}/r/{owner}/{name}` and the commit URL resolve their
 * document id and forward INTO `/po/...`.
 * People hold the readable URL, so that is the one that redirects.
 */
@Component({
  selector: 'app-po-detail-page',
  imports: [
    SparkPoDetailComponent,
    RepoBadgePanelComponent, RepoTrendPanelComponent, RepoCommitGraphPanelComponent, RepoMergeQueuePanelComponent, RepoSetupPanelComponent,
    CommitFilesExtrasComponent, HomeExtrasComponent, AccountDependencyGraphPanelComponent, AccountPermissionsNoticeComponent,
  ],
  template: `
    <spark-po-detail [beforeQueriesTemplate]="lead" [extraContentTemplate]="extras" />

    <ng-template #lead let-po let-entityType="entityType">
      @if (entityType.name === 'Account') {
        @if (accountOf(po); as account) {
          <!-- mx-3 lines it up with the attribute cards (m-3); the notice keeps its own classes, it also renders on the setup guide. -->
          <app-account-permissions-notice class="d-block mx-3" [provider]="account.provider" [login]="account.login" />
        }
      }
    </ng-template>

    <ng-template #extras let-po let-entityType="entityType">
      @if (entityType.name === 'Repository') {
        @if (repoOf(po); as repo) {
          <app-repo-badge-panel [provider]="repo.provider" [owner]="repo.owner" [name]="repo.name" />
          <app-repo-trend-panel [provider]="repo.provider" [owner]="repo.owner" [name]="repo.name" />
          <app-repo-commit-graph-panel [provider]="repo.provider" [owner]="repo.owner" [name]="repo.name" />
          <app-repo-merge-queue-panel [provider]="repo.provider" [owner]="repo.owner" [name]="repo.name" />
          <app-repo-setup-panel />
        }
      } @else if (entityType.name === 'Account') {
        @if (accountOf(po); as account) {
          <app-account-dependency-graph-panel [provider]="account.provider" [login]="account.login" />
        }
      } @else if (entityType.name === 'Commit') {
        <app-commit-files-extras [po]="po" />
      } @else if (entityType.name === 'Home') {
        <app-home-extras />
      }
    </ng-template>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export default class PoDetailPageComponent {
  repoOf(po: PersistentObject): { provider: string; owner: string; name: string } | null {
    const fullName = valueFor(po, 'FullName')?.value;
    if (typeof fullName !== 'string') return null;
    const [owner, name] = fullName.split('/');

    // ⚠️ The provider comes from the document id ("Repositories/github/123"), not from the Provider
    // enum. Provider serialises as "GitHub", and lowercasing it to reach the URL spelling would work
    // only by coincidence of how these three are spelled. The id holds the canonical form, and
    // ships whatever the caller may read (OwnerKey, which used to be read here, is denied, #264).
    const provider = forgeOf(po) ?? '';

    return owner && name && provider ? { provider, owner, name } : null;
  }

  /** The forge (from the document id, like `repoOf`) and login an Account's dependency graph is fetched by. */
  accountOf(po: PersistentObject): { provider: string; login: string } | null {
    const login = valueFor(po, 'Login')?.value;
    const provider = forgeOf(po);
    return typeof login === 'string' && login && provider ? { provider, login } : null;
  }

}
