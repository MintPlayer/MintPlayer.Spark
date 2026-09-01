import { ChangeDetectionStrategy, Component } from '@angular/core';
import { SparkPoDetailComponent } from '@mintplayer/ng-spark/po-detail';
import type { PersistentObject } from '@mintplayer/ng-spark/models';
import { valueFor } from '@mintplayer/ng-spark/models';
import { RepoBadgePanelComponent } from '../components/repo-badge-panel/repo-badge-panel.component';
import { RepoGatePanelComponent } from '../components/repo-gate-panel/repo-gate-panel.component';
import { RepoTrendPanelComponent } from '../components/repo-trend-panel/repo-trend-panel.component';
import { RepoSetupPanelComponent } from '../components/repo-setup-panel/repo-setup-panel.component';
import { AccountTokensPanelComponent } from '../components/account-tokens-panel/account-tokens-panel.component';
import { CommitFilesExtrasComponent } from './commit-files-extras.component';
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
 * ⚠️ It no longer forwards anything. Until preview.67 this component pre-fetched the whole
 * PersistentObject on every navigation just to decide whether Account should redirect to a
 * hand-written `/a/{login}` page — a wasted round-trip on every detail view of every type.
 * The vanity URLs now point the other way, as guards in `vanity-redirects.ts`: `/a/{login}`,
 * `/r/{owner}/{name}` and the commit URL resolve their document id and forward INTO `/po/...`.
 * People hold the readable URL, so that is the one that redirects.
 */
@Component({
  selector: 'app-po-detail-page',
  imports: [
    SparkPoDetailComponent,
    RepoBadgePanelComponent, RepoGatePanelComponent, RepoTrendPanelComponent, RepoSetupPanelComponent,
    AccountTokensPanelComponent, CommitFilesExtrasComponent, HomeExtrasComponent,
  ],
  template: `
    <spark-po-detail [extraContentTemplate]="extras" />

    <ng-template #extras let-po let-entityType="entityType">
      @if (entityType.name === 'Repository') {
        @if (repoOf(po); as repo) {
          <app-repo-badge-panel [owner]="repo.owner" [name]="repo.name" />
          <app-repo-gate-panel [owner]="repo.owner" [name]="repo.name" />
          <app-repo-trend-panel [owner]="repo.owner" [name]="repo.name" />
          <app-repo-setup-panel />
        }
      } @else if (entityType.name === 'Commit') {
        <app-commit-files-extras [po]="po" />
      } @else if (entityType.name === 'Home') {
        <app-home-extras />
      } @else if (entityType.name === 'Account') {
        @if (loginOf(po); as login) {
          <app-account-tokens-panel [login]="login" />
        }
      }
    </ng-template>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export default class PoDetailPageComponent {
  repoOf(po: PersistentObject): { owner: string; name: string } | null {
    const fullName = valueFor(po, 'FullName')?.value;
    if (typeof fullName !== 'string') return null;
    const [owner, name] = fullName.split('/');
    return owner && name ? { owner, name } : null;
  }

  loginOf(po: PersistentObject): string | null {
    const login = valueFor(po, 'Login')?.value;
    return typeof login === 'string' && login ? login : null;
  }
}
