import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { DependencyFleet, DependencyRepoStatus } from '../../services/browse.service';
import { BrowseServiceStub, createBrowseStub, provideBrowseStub, settle } from '../../../testing/test-utils';
import { AccountDependencyUpdatesPanelComponent, PACKAGES_SHOWN } from './account-dependency-updates-panel.component';

function repo(name: string, extra: Partial<DependencyRepoStatus> = {}): DependencyRepoStatus {
  return {
    id: `Repositories/github/${name.length}`, fullName: `acme/${name}`, name, isPrivate: false, enabled: true, health: 'ok', state: 'Idle',
    outcome: 'green', outcomeMessage: 'enqueued', lastRunAtUtc: '2026-10-12T06:10:00Z', lastFinishedAtUtc: '2026-10-12T07:00:00Z',
    nextSlotUtc: '2026-10-19T06:10:00Z', schedule: 'Mondays at 06:00 UTC', pullRequest: null, onboarding: 'Done', onboardingNote: null,
    holds: [], awaitingApproval: [], plannedGroups: 0, pins: [], coverageNote: null, ...extra,
  };
}

const fleet: DependencyFleet = {
  enabled: true,
  repositories: [
    repo('lib', { pullRequest: { number: 41, url: 'https://github.com/acme/lib/pull/41', draft: false }, plannedGroups: 2 }),
    repo('app', {
      health: 'red', outcome: 'bisected', outcomeMessage: 'the culprit is @angular/*',
      holds: [{ kind: 'Unattributed', group: '@angular/*', depNames: ['@angular/core'], reason: 'CI went red with it; found by bisect', by: null, createdAtUtc: '2026-10-12T07:00:00Z' }],
      awaitingApproval: [{ group: 'typescript', depName: 'typescript', current: '5.9.2', target: '6.0.3', command: 'approve typescript' }],
    }),
    repo('tool', { health: 'blocked', onboarding: 'Blocked', onboardingNote: 'The installation has not accepted Workflows: write yet.' }),
  ],
  packages: [
    { ecosystem: 'npm', name: '@angular/core', uses: [
      { repositoryId: 'Repositories/github/3', fullName: 'acme/app', version: '21.2.0', path: 'package.json', dev: false, status: 'held: CI went red with it' },
      { repositoryId: 'Repositories/github/3', fullName: 'acme/lib', version: '22.0.1', path: 'package.json', dev: false, status: null },
    ] },
  ],
  packagesTruncated: false,
  readiness: [{
    platform: 'Angular', anchor: '@angular/core', nextMajor: '22', ready: 1, total: 2,
    repositories: [
      { repositoryId: 'Repositories/github/3', fullName: 'acme/app', current: '21.2.0', status: 'blocked', detail: null, blockers: ['@nx/angular'] },
      { repositoryId: 'Repositories/github/4', fullName: 'acme/lib', current: '22.0.1', status: 'on-target', detail: null, blockers: [] },
    ],
    blockers: [{ name: '@nx/angular', repositories: 1 }],
  }],
};

describe('AccountDependencyUpdatesPanelComponent', () => {
  let fixture: ComponentFixture<AccountDependencyUpdatesPanelComponent>;
  let browse: BrowseServiceStub;

  async function create(getDependencyFleet: () => Promise<DependencyFleet>) {
    browse = createBrowseStub({ getDependencyFleet });
    TestBed.configureTestingModule({
      imports: [AccountDependencyUpdatesPanelComponent],
      providers: [provideZonelessChangeDetection(), provideNoopAnimations(), provideRouter([]), provideBrowseStub(browse)],
    });
    fixture = TestBed.createComponent(AccountDependencyUpdatesPanelComponent);
    fixture.componentRef.setInput('provider', 'github');
    fixture.componentRef.setInput('login', 'acme');
    await settle(fixture);
  }

  const el = () => fixture.nativeElement as HTMLElement;
  const q = (selector: string) => el().querySelector(selector) as HTMLElement | null;

  it('shows readiness per platform major, each repository, and which repository is on which version', async () => {
    await create(() => Promise.resolve(fleet));

    expect(browse.getDependencyFleet).toHaveBeenCalledWith('github', 'acme');
    const readiness = q('[data-testid="du-readiness"]')!;
    expect(readiness.textContent).toContain('Angular 22');
    expect(readiness.textContent).toContain('1 of 2 ready');
    expect(readiness.textContent).toContain('@nx/angular');

    const rows = [...el().querySelectorAll('[data-testid="du-repositories"] tbody tr')];
    expect(rows.map(r => r.getAttribute('data-repo'))).toEqual(['acme/lib', 'acme/app', 'acme/tool']);
    expect(rows[0].querySelector('a[href="https://github.com/acme/lib/pull/41"]')).not.toBeNull();
    expect(rows[1].textContent).toContain('CI red');
    expect(rows[1].textContent).toContain('the culprit is @angular/*');
    expect(rows[1].textContent).toContain('approve typescript');
    expect(rows[1].textContent).toContain('found by bisect');
    expect(rows[2].textContent).toContain('onboarding blocked');
    expect(rows[2].querySelector('[data-testid="du-why"]')?.textContent).toContain('Workflows: write');
    expect(rows[0].querySelector('[data-testid="du-why"]')).toBeNull();

    const packages = q('[data-testid="du-packages"]')!;
    expect(packages.textContent).toContain('@angular/core');
    expect(packages.textContent).toContain('acme/app 21.2.0');
    expect(packages.textContent).toContain('acme/lib 22.0.1');
  });

  it('shows each state as a chip, a subtle background with emphasis text', async () => {
    await create(() => Promise.resolve(fleet));

    const states = [...el().querySelectorAll('[data-testid="du-state"]')].map(s => [...s.classList].sort().join(' '));
    expect(states[1]).toBe('bg-danger-subtle border border-danger-subtle du-state text-danger-emphasis');
  });

  it('lists the first packages and the rest on request', async () => {
    const many = Array.from({ length: PACKAGES_SHOWN + 3 }, (_, i) => ({ ecosystem: 'npm', name: `pkg-${i}`, uses: fleet.packages[0].uses }));
    await create(() => Promise.resolve({ ...fleet, packages: many }));

    expect(el().querySelectorAll('[data-testid="du-packages"] tbody tr')).toHaveLength(PACKAGES_SHOWN);
    const more = [...el().querySelectorAll('button')].find(b => b.textContent?.includes('Show 3 more'))!;
    more.click();
    await settle(fixture);
    expect(el().querySelectorAll('[data-testid="du-packages"] tbody tr')).toHaveLength(PACKAGES_SHOWN + 3);
  });

  it('is hidden when the server refuses, and when nothing on the account uses dependency updates', async () => {
    await create(() => Promise.reject(new Error('404')));
    expect(q('[data-testid="dependency-fleet"]')).toBeNull();

    TestBed.resetTestingModule();
    await create(() => Promise.resolve({ ...fleet, enabled: false, repositories: [repo('lib', { enabled: false, health: 'off', outcome: null })] }));
    expect(q('[data-testid="dependency-fleet"]')).toBeNull();
  });
});
