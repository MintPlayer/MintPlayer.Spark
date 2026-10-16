import { Component, input, provideZonelessChangeDetection, TemplateRef } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { beforeEach, describe, expect, it } from 'vitest';
import type { PersistentObject } from '@mintplayer/ng-spark/models';
import { SparkPoDetailComponent } from '@mintplayer/ng-spark/po-detail';
import { RepoBadgePanelComponent } from '../components/repo-badge-panel/repo-badge-panel.component';
import { RepoTrendPanelComponent } from '../components/repo-trend-panel/repo-trend-panel.component';
import { RepoSetupPanelComponent } from '../components/repo-setup-panel/repo-setup-panel.component';
import { RepoCommitGraphPanelComponent } from '../components/repo-commit-graph-panel/repo-commit-graph-panel.component';
import { RepoMergeQueuePanelComponent } from '../components/repo-merge-queue-panel/repo-merge-queue-panel.component';
import { AccountDependencyGraphPanelComponent } from '../components/account-dependency-graph-panel/account-dependency-graph-panel.component';
import { AccountDependencyUpdatesPanelComponent } from '../components/account-dependency-updates-panel/account-dependency-updates-panel.component';
import { RepoDependencyUpdatesPanelComponent } from '../components/repo-dependency-updates-panel/repo-dependency-updates-panel.component';
import { AccountPermissionsNoticeComponent } from '../components/account-permissions-notice/account-permissions-notice.component';
import { CommitFilesExtrasComponent } from './commit-files-extras.component';
import { HomeExtrasComponent } from './home-extras.component';
import PoDetailPageComponent from './po-detail-page.component';

function po(attributes: Record<string, unknown>, id = 'items/1'): PersistentObject {
  return {
    id,
    attributes: Object.entries(attributes).map(([name, value]) => ({ name, value })),
  } as unknown as PersistentObject;
}

/** What the stand-in detail page hands the extras template, set per test. */
let extrasContext: { $implicit: PersistentObject; entityType: { name: string } };

/**
 * Stands in for spark-po-detail: renders only the before-queries and extras slots, each in its own
 * marked wrapper, with the context the real one passes.
 */
@Component({
  selector: 'spark-po-detail',
  imports: [NgTemplateOutlet],
  template: `
    <div class="slot-before-queries">@if (beforeQueriesTemplate(); as tpl) { <ng-container *ngTemplateOutlet="tpl; context: context" /> }</div>
    <div class="slot-extras">@if (extraContentTemplate(); as tpl) { <ng-container *ngTemplateOutlet="tpl; context: context" /> }</div>
  `,
})
class StubPoDetail {
  readonly beforeQueriesTemplate = input<TemplateRef<unknown> | null>(null);
  readonly extraContentTemplate = input<TemplateRef<unknown> | null>(null);
  readonly context = extrasContext;
}

@Component({ selector: 'app-repo-badge-panel', template: '' })
class StubBadgePanel {
  readonly provider = input<string>();
  readonly owner = input<string>();
  readonly name = input<string>();
}

@Component({ selector: 'app-repo-trend-panel', template: '' })
class StubTrendPanel {
  readonly provider = input<string>();
  readonly owner = input<string>();
  readonly name = input<string>();
}

@Component({ selector: 'app-repo-commit-graph-panel', template: '' })
class StubCommitGraphPanel {
  readonly provider = input<string>();
  readonly owner = input<string>();
  readonly name = input<string>();
}

@Component({ selector: 'app-repo-merge-queue-panel', template: '' })
class StubMergeQueuePanel {
  readonly provider = input<string>();
  readonly owner = input<string>();
  readonly name = input<string>();
}

@Component({ selector: 'app-repo-setup-panel', template: '' })
class StubSetupPanel {}

@Component({ selector: 'app-commit-files-extras', template: '' })
class StubCommitFilesExtras {
  readonly po = input<PersistentObject>();
}

@Component({ selector: 'app-home-extras', template: '' })
class StubHomeExtras {}

@Component({ selector: 'app-account-dependency-graph-panel', template: '' })
class StubDependencyGraphPanel {
  readonly provider = input<string>();
  readonly login = input<string>();
}

@Component({ selector: 'app-account-dependency-updates-panel', template: '' })
class StubDependencyFleetPanel {
  readonly provider = input<string>();
  readonly login = input<string>();
}

@Component({ selector: 'app-repo-dependency-updates-panel', template: '' })
class StubRepoDependencyPanel {
  readonly provider = input<string>();
  readonly owner = input<string>();
  readonly name = input<string>();
  readonly po = input<unknown>();
}

@Component({ selector: 'app-account-permissions-notice', template: '' })
class StubPermissionsNotice {
  readonly provider = input<string>();
  readonly login = input<string>();
}

describe('PoDetailPageComponent', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
    TestBed.overrideComponent(PoDetailPageComponent, {
      remove: {
        imports: [SparkPoDetailComponent, RepoBadgePanelComponent, RepoTrendPanelComponent, RepoCommitGraphPanelComponent, RepoMergeQueuePanelComponent,
          RepoSetupPanelComponent, CommitFilesExtrasComponent, HomeExtrasComponent, AccountDependencyGraphPanelComponent, AccountPermissionsNoticeComponent,
          AccountDependencyUpdatesPanelComponent, RepoDependencyUpdatesPanelComponent],
      },
      add: {
        imports: [StubPoDetail, StubBadgePanel, StubTrendPanel, StubCommitGraphPanel, StubMergeQueuePanel, StubSetupPanel, StubCommitFilesExtras,
          StubHomeExtras, StubDependencyGraphPanel, StubPermissionsNotice, StubDependencyFleetPanel, StubRepoDependencyPanel],
      },
    });
  });

  function render(entityType: string, object: PersistentObject): ComponentFixture<PoDetailPageComponent> {
    extrasContext = { $implicit: object, entityType: { name: entityType } };
    const fixture = TestBed.createComponent(PoDetailPageComponent);
    fixture.detectChanges();
    return fixture;
  }

  function has(fixture: ComponentFixture<unknown>, type: unknown): boolean {
    return fixture.debugElement.query(By.directive(type as never)) !== null;
  }

  it('mounts the badge, trend, commit-graph, merge-queue and setup panels on a Repository, scoped to its forge', () => {
    const fixture = render('Repository', po({ FullName: 'acme/widgets' }, 'Repositories/gitlab/1'));

    for (const type of [StubBadgePanel, StubTrendPanel, StubCommitGraphPanel, StubMergeQueuePanel, StubRepoDependencyPanel]) {
      const panel = fixture.debugElement.query(By.directive(type)).componentInstance as StubBadgePanel;
      expect([panel.provider(), panel.owner(), panel.name()]).toEqual(['gitlab', 'acme', 'widgets']);
    }
    // The dependency panel also gets the object itself, for its "Run the scheduled slot now" detail action (R28).
    expect((fixture.debugElement.query(By.directive(StubRepoDependencyPanel)).componentInstance as StubRepoDependencyPanel).po())
      .toEqual(expect.objectContaining({ id: 'Repositories/gitlab/1' }));
    expect(has(fixture, StubDependencyFleetPanel)).toBe(false);
    expect(has(fixture, StubSetupPanel)).toBe(true);
    expect(has(fixture, StubCommitFilesExtras)).toBe(false);
    expect(has(fixture, StubHomeExtras)).toBe(false);
  });

  it('mounts no repository panel when the repository has no forge or no owner/name', () => {
    for (const object of [po({ FullName: 'acme/widgets' }), po({ FullName: 'widgets' }, 'Repositories/github/1'), po({}, 'Repositories/github/1')]) {
      const fixture = render('Repository', object);
      expect(has(fixture, StubBadgePanel)).toBe(false);
      expect(has(fixture, StubSetupPanel)).toBe(false);
    }
  });

  it('mounts the files bridge on a Commit, handing it the object', () => {
    const commit = po({ Sha: 'abc' });
    const fixture = render('Commit', commit);

    const bridge = fixture.debugElement.query(By.directive(StubCommitFilesExtras)).componentInstance as StubCommitFilesExtras;
    expect(bridge.po()).toBe(commit);
    expect(has(fixture, StubBadgePanel)).toBe(false);
  });

  it('mounts the Home extras on Home, and no repository, commit or home panel on an Account', () => {
    expect(has(render('Home', po({})), StubHomeExtras)).toBe(true);

    const account = render('Account', po({ FullName: 'acme/widgets' }, 'Accounts/github/1'));
    for (const type of [StubBadgePanel, StubTrendPanel, StubCommitGraphPanel, StubMergeQueuePanel, StubSetupPanel, StubCommitFilesExtras, StubHomeExtras]) {
      expect(has(account, type)).toBe(false);
    }
  });

  it('mounts the dependency graph on an Account, scoped to its forge and login', () => {
    const fixture = render('Account', po({ Login: 'acme', Provider: 'GitLab' }, 'Accounts/gitlab/7'));
    const panel = fixture.debugElement.query(By.directive(StubDependencyGraphPanel)).componentInstance as StubDependencyGraphPanel;
    expect([panel.provider(), panel.login()]).toEqual(['gitlab', 'acme']);
    // The pending-permissions notice (O17) is scoped the same way, but mounted through the
    // before-queries slot so it sits above the repositories grid, not among the extras below it.
    const notice = fixture.debugElement.query(By.directive(StubPermissionsNotice));
    expect([(notice.componentInstance as StubPermissionsNotice).provider(), (notice.componentInstance as StubPermissionsNotice).login()])
      .toEqual(['gitlab', 'acme']);
    expect((notice.nativeElement as HTMLElement).closest('.slot-before-queries')).not.toBeNull();
    expect(fixture.debugElement.query(By.css('.slot-extras')).query(By.directive(StubPermissionsNotice))).toBeNull();
    // The dependency graph stays an extra, and the fleet view follows it, scoped the same way.
    expect(fixture.debugElement.query(By.css('.slot-extras')).query(By.directive(StubDependencyGraphPanel))).not.toBeNull();
    const fleet = fixture.debugElement.query(By.directive(StubDependencyFleetPanel)).componentInstance as StubDependencyFleetPanel;
    expect([fleet.provider(), fleet.login()]).toEqual(['gitlab', 'acme']);
    expect(has(fixture, StubRepoDependencyPanel)).toBe(false);

    for (const object of [po({ Login: 'acme' }), po({}, 'Accounts/github/1'), po({ Login: 3 }, 'Accounts/github/1')]) {
      expect(has(render('Account', object), StubDependencyGraphPanel)).toBe(false);
    }
    expect(has(render('Repository', po({ FullName: 'acme/widgets', Login: 'acme' }, 'Repositories/github/1')), StubDependencyGraphPanel)).toBe(false);
  });

  // Provider serialises as "GitHub"; only the document id holds the URL spelling.
  it('repoOf reads the forge from the document id, not from the Provider enum', () => {
    const page = TestBed.createComponent(PoDetailPageComponent).componentInstance;

    expect(page.repoOf(po({ FullName: 'acme/widgets', Provider: 'GitHub' }, 'Repositories/github/1')))
      .toEqual({ provider: 'github', owner: 'acme', name: 'widgets' });
    expect(page.repoOf(po({ FullName: 'acme/widgets', Provider: 'GitHub' }))).toBeNull();
    expect(page.repoOf(po({ FullName: 42 }, 'Repositories/github/1'))).toBeNull();
  });
});
