import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { SparkNotificationService } from '@mintplayer/ng-spark/client-operations';
import { PersistentObject } from '@mintplayer/ng-spark/models';
import { SparkLanguageService, SparkService } from '@mintplayer/ng-spark/services';
import { describe, expect, it, vi } from 'vitest';
import { DependencyRepoView } from '../../services/browse.service';
import { BrowseServiceStub, createBrowseStub, provideBrowseStub, settle } from '../../../testing/test-utils';
import { RepoDependencyUpdatesPanelComponent, RUN_NOW_ACTION } from './repo-dependency-updates-panel.component';

const view: DependencyRepoView = {
  status: {
    id: 'Repositories/github/1', fullName: 'acme/widget', name: 'widget', isPrivate: false, enabled: true, health: 'waiting', state: 'AwaitingChecks',
    outcome: null, outcomeMessage: null, lastRunAtUtc: '2026-10-12T06:10:00Z', lastFinishedAtUtc: null, nextSlotUtc: '2026-10-19T06:10:00Z',
    schedule: 'Mondays at 06:00 UTC', pullRequest: { number: 41, url: 'https://github.com/acme/widget/pull/41', draft: true },
    onboarding: 'Done', onboardingNote: null,
    holds: [{ kind: 'Human', group: null, depNames: ['chalk'], reason: 'chalk held at 4.1.1 (set by @octocat)', by: 'octocat', createdAtUtc: '2026-10-12T06:30:00Z' }],
    awaitingApproval: [{ group: '@angular/*', depName: '@angular/core', current: '21.2.0', target: '22.0.1', command: 'approve @angular/*' }],
    plannedGroups: 1, pins: ['lodash 4.17.21 (package.json)'], coverageNote: null,
  },
  planned: [{ group: 'lodash', level: 'patch', autoMerge: false, items: [{ depName: 'lodash', packageFile: 'package.json', from: '4.17.20', to: '4.17.21' }] }],
  history: [{ atUtc: '2026-10-12T06:40:00Z', text: 'pushed mintplayer/dependencies at c3c3c3c3c3c3' }],
};

describe('RepoDependencyUpdatesPanelComponent', () => {
  let fixture: ComponentFixture<RepoDependencyUpdatesPanelComponent>;
  let browse: BrowseServiceStub;
  let spark: { getCustomActions: ReturnType<typeof vi.fn>; executeCustomAction: ReturnType<typeof vi.fn> };

  const repositoryPo = { id: 'Repositories/github/1', objectTypeId: 'type-repository' } as PersistentObject;

  async function create(getRepoDependencyUpdates: () => Promise<DependencyRepoView>, options: { po?: PersistentObject; actions?: string[] } = {}) {
    browse = createBrowseStub({ getRepoDependencyUpdates });
    spark = {
      getCustomActions: vi.fn(() => Promise.resolve((options.actions ?? [RUN_NOW_ACTION]).map(name => ({ name })))),
      executeCustomAction: vi.fn(() => Promise.resolve({ outcome: 'started' })),
    };
    TestBed.configureTestingModule({
      imports: [RepoDependencyUpdatesPanelComponent],
      providers: [
        provideZonelessChangeDetection(), provideNoopAnimations(), provideRouter([]), provideBrowseStub(browse),
        { provide: SparkService, useValue: spark },
        { provide: SparkLanguageService, useValue: { t: (key: string, fallback?: string) => fallback ?? key, resolve: () => '' } },
        { provide: SparkNotificationService, useValue: { show: vi.fn() } },
      ],
    });
    fixture = TestBed.createComponent(RepoDependencyUpdatesPanelComponent);
    fixture.componentRef.setInput('provider', 'github');
    fixture.componentRef.setInput('owner', 'acme');
    fixture.componentRef.setInput('name', 'widget');
    if (options.po) fixture.componentRef.setInput('po', options.po);
    await settle(fixture);
  }

  const el = () => fixture.nativeElement as HTMLElement;
  const q = (selector: string) => el().querySelector(selector) as HTMLElement | null;

  it('shows the rolling pull request, the next slot, holds, approvals waiting, pins and what is applied', async () => {
    await create(() => Promise.resolve(view));

    expect(browse.getRepoDependencyUpdates).toHaveBeenCalledWith('github', 'acme', 'widget');
    expect(q('[data-testid="du-state"]')?.textContent).toContain('waiting for CI');
    expect(q('[data-testid="du-pr"]')?.getAttribute('href')).toBe('https://github.com/acme/widget/pull/41');
    expect(q('[data-testid="du-summary"]')?.textContent).toContain('draft');
    expect(q('[data-testid="du-next"]')?.textContent).toContain('Mondays at 06:00 UTC');
    expect(q('[data-testid="du-holds"]')?.textContent).toContain('chalk held at 4.1.1');
    expect(q('[data-testid="du-awaiting"]')?.textContent).toContain('approve @angular/*');
    expect(q('[data-testid="du-pins"]')?.textContent).toContain('lodash 4.17.21');
    expect(q('[data-testid="du-planned"]')?.textContent).toContain('4.17.21');
    expect(q('[data-testid="du-history"]')?.textContent).toContain('pushed mintplayer/dependencies');
  });

  it('shows why onboarding is blocked', async () => {
    await create(() => Promise.resolve({ ...view, status: { ...view.status, health: 'blocked', onboarding: 'Blocked', onboardingNote: 'The App does not request Dependabot alerts: read.' } }));

    expect(q('[data-testid="du-state"]')?.textContent).toContain('onboarding blocked');
    expect(q('[data-testid="du-onboarding"]')?.textContent).toContain('Dependabot alerts');
  });

  it('is hidden when the server refuses, and when the updates are off and never ran', async () => {
    await create(() => Promise.reject(new Error('404')));
    expect(q('[data-testid="dependency-updates"]')).toBeNull();

    TestBed.resetTestingModule();
    await create(() => Promise.resolve({ planned: [], history: [], status: { ...view.status, enabled: false, health: 'off', outcome: null, pullRequest: null } }));
    expect(q('[data-testid="dependency-updates"]')).toBeNull();
    // Off but readable: the way in is the onboarding page (R20).
    expect(q('[data-testid="du-onboard-link"]')?.getAttribute('href')).toBe('/dependency-updates/onboard');
  });

  it('shows no onboarding link when the server refuses the repository', async () => {
    await create(() => Promise.reject(new Error('404')));
    expect(q('[data-testid="du-off"]')).toBeNull();
  });

  // R26, R30/D-O7: the onboarding pull request, coverage.yml's real merge queue, and a message waiting for the rate limit.
  it('shows the onboarding pull request, the merge queue in coverage.yml and the rate-limit wait', async () => {
    await create(() => Promise.resolve({ ...view, status: {
      ...view.status, onboardingPullRequest: { number: 1, url: 'https://github.com/acme/widget/pull/1', draft: false },
      mergeQueueInFile: 'enabled', rateLimit: { untilUtc: '2026-10-16T17:45:00Z', park: 3, budget: 25 },
    } }));

    expect(q('[data-testid="du-onboarding-pr"]')?.getAttribute('href')).toBe('https://github.com/acme/widget/pull/1');
    expect(q('[data-testid="du-merge-queue-file"]')?.textContent).toContain('enabled');
    expect(q('[data-testid="du-rate-limit"]')?.textContent).toMatch(/waiting for GitHub's rate limit until \S+.*\(park 3 of 25\)/);
  });

  it('has no rate-limit line when nothing waits', async () => {
    await create(() => Promise.resolve(view));
    expect(q('[data-testid="du-rate-limit"]')).toBeNull();
  });

  // R28: the detail action, with the Repository as its parent; the server's toast says how it went.
  it('runs the scheduled slot now through the Repository detail action, then reloads', async () => {
    await create(() => Promise.resolve(view), { po: repositoryPo });

    expect(spark.getCustomActions).toHaveBeenCalledWith('type-repository');
    q('[data-testid="du-run-now"]')!.click();
    await settle(fixture);

    expect(spark.executeCustomAction).toHaveBeenCalledWith('type-repository', RUN_NOW_ACTION, repositoryPo);
    expect(browse.getRepoDependencyUpdates).toHaveBeenCalledTimes(2);
  });

  it('offers no run-now button without the object, or when the server does not offer the action', async () => {
    await create(() => Promise.resolve(view));
    expect(q('[data-testid="du-run-now"]')).toBeNull();

    TestBed.resetTestingModule();
    await create(() => Promise.resolve(view), { po: repositoryPo, actions: [] });
    expect(q('[data-testid="du-run-now"]')).toBeNull();
  });
});
