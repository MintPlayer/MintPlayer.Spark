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

/** Stands in for spark-po-detail: renders only the extras slot, with the context the real one passes. */
@Component({
  selector: 'spark-po-detail',
  imports: [NgTemplateOutlet],
  template: `@if (extraContentTemplate(); as tpl) { <ng-container *ngTemplateOutlet="tpl; context: context" /> }`,
})
class StubPoDetail {
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

@Component({ selector: 'app-repo-setup-panel', template: '' })
class StubSetupPanel {}

@Component({ selector: 'app-commit-files-extras', template: '' })
class StubCommitFilesExtras {
  readonly po = input<PersistentObject>();
}

@Component({ selector: 'app-home-extras', template: '' })
class StubHomeExtras {}

describe('PoDetailPageComponent', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
    TestBed.overrideComponent(PoDetailPageComponent, {
      remove: {
        imports: [SparkPoDetailComponent, RepoBadgePanelComponent, RepoTrendPanelComponent, RepoSetupPanelComponent,
          CommitFilesExtrasComponent, HomeExtrasComponent],
      },
      add: { imports: [StubPoDetail, StubBadgePanel, StubTrendPanel, StubSetupPanel, StubCommitFilesExtras, StubHomeExtras] },
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

  it('mounts the badge, trend and setup panels on a Repository, scoped to its forge', () => {
    const fixture = render('Repository', po({ FullName: 'acme/widgets' }, 'Repositories/gitlab/1'));

    for (const type of [StubBadgePanel, StubTrendPanel]) {
      const panel = fixture.debugElement.query(By.directive(type)).componentInstance as StubBadgePanel;
      expect([panel.provider(), panel.owner(), panel.name()]).toEqual(['gitlab', 'acme', 'widgets']);
    }
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

  it('mounts the Home extras on Home, and nothing on any other type', () => {
    expect(has(render('Home', po({})), StubHomeExtras)).toBe(true);

    const account = render('Account', po({ FullName: 'acme/widgets' }, 'Accounts/github/1'));
    for (const type of [StubBadgePanel, StubTrendPanel, StubSetupPanel, StubCommitFilesExtras, StubHomeExtras]) {
      expect(has(account, type)).toBe(false);
    }
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
