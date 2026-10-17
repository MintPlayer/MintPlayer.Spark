import { Component, input, output, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { SparkQueryCardComponent } from '@mintplayer/ng-spark/grid';
import type { QueryResultItem } from '@mintplayer/ng-spark/models';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { describe, expect, it } from 'vitest';
import { BrowseServiceStub, createBrowseStub, provideBrowseStub, settle } from '../../../testing/test-utils';
import { DependencyOnboardingSummary } from '../../services/browse.service';
import DependencyUpdatesOnboardComponent, { ONBOARDING_QUERY, ONBOARDING_SCOPE_TYPE } from './dependency-updates-onboard.component';

/**
 * Stands in for ng-spark's card, which owns the rows, the search, the paging, the selection and the query action (D-O14):
 * these specs are about what the page hands it.
 */
@Component({ selector: 'spark-query-card', template: '' })
class SparkQueryCardStub {
  queryId = input.required<string>();
  parentId = input<string>('');
  parentType = input<string>('');
  rowRoute = input<((row: QueryResultItem) => unknown[] | null) | null>(null);
  customActionExecuted = output<{ action: unknown }>();
}

const summary = (over: Partial<DependencyOnboardingSummary> = {}): DependencyOnboardingSummary => ({
  login: 'octocat', credentialState: 'Ok', linked: true, narrowed: false, setupAction: null, repositories: 3, syncing: 0, ...over,
});

const WORDS: Record<string, string> = {
  'dependencyOnboardingUi.syncing': '{0} of these repositories are still syncing with GitHub; they appear here in a moment.',
  'dependencyOnboardingUi.whoMayEnable': 'Only organization owners and repository admins can enable dependency updates.',
};

describe('DependencyUpdatesOnboardComponent', () => {
  async function open(url: string, header: () => Promise<DependencyOnboardingSummary> = () => Promise.resolve(summary())) {
    const browse: BrowseServiceStub = createBrowseStub({ getDependencyOnboarding: header });
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(), provideNoopAnimations(),
        provideRouter([{ path: 'dependency-updates/onboard', component: DependencyUpdatesOnboardComponent }]),
        provideBrowseStub(browse),
        { provide: SparkLanguageService, useValue: { t: (key: string, fallback?: string) => WORDS[key] ?? fallback ?? key, resolve: () => '' } },
      ],
    });
    TestBed.overrideComponent(DependencyUpdatesOnboardComponent, {
      remove: { imports: [SparkQueryCardComponent] },
      add: { imports: [SparkQueryCardStub] },
    });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url, DependencyUpdatesOnboardComponent);
    await settle(harness.fixture);
    const el = () => harness.fixture.nativeElement as HTMLElement;
    const q = (selector: string) => el().querySelector<HTMLElement>(selector);
    const card = () => harness.fixture.debugElement.query(By.directive(SparkQueryCardStub))?.componentInstance as SparkQueryCardStub | undefined;
    return { harness, browse, el, q, card };
  }

  it('embeds the Spark query with its action instead of a list of its own', async () => {
    const { card, el } = await open('/dependency-updates/onboard');

    expect(card()?.queryId()).toBe(ONBOARDING_QUERY);
    // No page-local table, checkboxes, enable button or result panel: the card brings all of it.
    expect(el().querySelector('table, bs-table, bs-checkbox, [data-testid="onboard-enable"], [data-testid="onboard-result"]')).toBeNull();
  });

  // R20/R32/D-O13: the setup redirect's query is the forge's business on the server; the page forwards it untouched.
  it('passes the setup redirect query to the grid as its scope parent and to the header, untouched', async () => {
    const { harness, browse, card, q } = await open('/dependency-updates/onboard?installation_id=42&setup_action=install',
      () => Promise.resolve(summary({ narrowed: true, setupAction: 'install' })));

    expect(browse.getDependencyOnboarding).toHaveBeenCalledWith('installation_id=42&setup_action=install');
    expect(card()?.parentId()).toBe('installation_id=42&setup_action=install');
    expect(card()?.parentType()).toBe(ONBOARDING_SCOPE_TYPE);
    expect(q('[data-testid="onboard-narrowed"]')).not.toBeNull();
    expect(q('[data-testid="onboard-all-accounts"]')?.getAttribute('href')).toBe('/dependency-updates/onboard');

    // "All my accounts": the same route without the query reuses the component, and the grid then has no parent at all.
    await harness.navigateByUrl('/dependency-updates/onboard');
    await settle(harness.fixture);
    expect(browse.getDependencyOnboarding).toHaveBeenLastCalledWith('');
    expect(card()?.parentId()).toBe('');
    expect(card()?.parentType()).toBe('');
  });

  it('keeps a repeated or empty installation parameter as it arrived, for the server to treat as unknown (D-O13)', async () => {
    const { browse, card } = await open('/dependency-updates/onboard?installation_id=5555&installation_id=6666');
    expect(card()?.parentId()).toBe('installation_id=5555&installation_id=6666');
    expect(browse.getDependencyOnboarding).toHaveBeenCalledWith('installation_id=5555&installation_id=6666');

    TestBed.resetTestingModule();
    const empty = await open('/dependency-updates/onboard?installation_id=');
    expect(empty.card()?.parentId()).toBe('installation_id=');
    expect(empty.card()?.parentType()).toBe(ONBOARDING_SCOPE_TYPE);
  });

  it('links a row to its repository page from the Repository document id', async () => {
    const { card } = await open('/dependency-updates/onboard');
    const route = card()!.rowRoute()!;

    const row = (id: string, fullName: unknown) => ({ id, values: [{ key: 'FullName', value: fullName }] }) as unknown as QueryResultItem;
    expect(route(row('Repositories/github/1', 'acme/widget'))).toEqual(['/', 'github', 'r', 'acme', 'widget']);
    expect(route(row('Repositories/github/1', null))).toBeNull();
    expect(route(row('Something/else', 'acme/widget'))).toBeNull();
  });

  it('shows how many repositories are still syncing above the grid', async () => {
    const { q, card } = await open('/dependency-updates/onboard', () => Promise.resolve(summary({ syncing: 2 })));

    expect(q('[data-testid="onboard-syncing"]')?.textContent).toContain('2 of these repositories are still syncing with GitHub');
    expect(card()).toBeDefined();
  });

  it('shows no syncing banner when nothing is syncing', async () => {
    const { q } = await open('/dependency-updates/onboard');
    expect(q('[data-testid="onboard-syncing"]')).toBeNull();
  });

  it('refreshes the header after the action ran (the grid refreshes itself)', async () => {
    const { harness, browse, card } = await open('/dependency-updates/onboard');
    const before = browse.getDependencyOnboarding.mock.calls.length;

    card()!.customActionExecuted.emit({ action: { name: 'EnableDependencyUpdates' } });
    await settle(harness.fixture);

    expect(browse.getDependencyOnboarding.mock.calls.length).toBe(before + 1);
  });

  it('says why there is nothing to list instead of an empty grid', async () => {
    const managed = await open('/dependency-updates/onboard', () => Promise.resolve(summary({ repositories: 0 })));
    expect(managed.q('[data-testid="onboard-empty"]')?.textContent).toContain('Only organization owners and repository admins');
    expect(managed.card()).toBeUndefined();

    TestBed.resetTestingModule();
    const narrowed = await open('/dependency-updates/onboard?installation_id=7', () => Promise.resolve(summary({ repositories: 0, narrowed: true })));
    expect(narrowed.q('[data-testid="onboard-empty"]')?.textContent).toContain('dependencyOnboardingUi.emptyNarrowed');
  });

  it('asks to link GitHub when the viewer has no forge sign-in, without a grid', async () => {
    const { q, card } = await open('/dependency-updates/onboard', () => Promise.resolve(summary({ linked: false, login: null, repositories: 0 })));

    expect(q('[data-testid="onboard-not-linked"]')).not.toBeNull();
    expect(q('[data-testid="onboard-empty"]')).toBeNull();
    expect(card()).toBeUndefined();
  });

  it('warns about an expired sign-in and an unreachable forge, and keeps the grid', async () => {
    const reauth = await open('/dependency-updates/onboard', () => Promise.resolve(summary({ credentialState: 'ReauthRequired' })));
    expect(reauth.q('[data-testid="onboard-reauth"]')).not.toBeNull();
    expect(reauth.card()).toBeDefined();

    TestBed.resetTestingModule();
    const unavailable = await open('/dependency-updates/onboard', () => Promise.resolve(summary({ credentialState: 'Unavailable' })));
    expect(unavailable.q('[data-testid="onboard-unavailable"]')).not.toBeNull();
  });

  it("says an install request waits for an organization owner's approval", async () => {
    const { q } = await open('/dependency-updates/onboard?installation_id=9&setup_action=request',
      () => Promise.resolve(summary({ narrowed: true, setupAction: 'request', repositories: 0 })));

    expect(q('[data-testid="onboard-request"]')).not.toBeNull();
  });

  it('keeps the grid when the header fails, and says so', async () => {
    const { q, card } = await open('/dependency-updates/onboard', () => Promise.reject(new Error('boom')));

    expect(q('[data-testid="onboard-error"]')).not.toBeNull();
    expect(card()?.queryId()).toBe(ONBOARDING_QUERY);
  });
});
