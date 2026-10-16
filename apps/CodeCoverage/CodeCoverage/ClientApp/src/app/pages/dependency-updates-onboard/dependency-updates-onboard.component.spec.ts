import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { SparkLanguageService, SparkQueryActionsService, SparkService } from '@mintplayer/ng-spark/services';
import { describe, expect, it, vi } from 'vitest';
import { BrowseServiceStub, createBrowseStub, provideBrowseStub, settle } from '../../../testing/test-utils';
import { DependencyOnboardingResponse, DependencyOnboardingResult, DependencyOnboardingRow } from '../../services/browse.service';
import DependencyUpdatesOnboardComponent, { ENABLE_ACTION, ONBOARDING_QUERY } from './dependency-updates-onboard.component';

const row = (over: Partial<DependencyOnboardingRow> = {}): DependencyOnboardingRow => ({
  repositoryId: 'Repositories/github/1', provider: 'github', fullName: 'acme/widget', accountLogin: 'acme', isPrivate: false,
  managedBy: 'owner', setting: 'Inherit', enabled: false, accountDefault: false, onboarding: 'None', onboardingNote: null,
  onboardingPullRequest: null, syncing: false, onboardingMergeQueue: true, mergeQueueInFile: null, ...over,
});

const response = (over: Partial<DependencyOnboardingResponse> = {}): DependencyOnboardingResponse => ({
  login: 'octocat', credentialState: 'Ok', linked: true, narrowed: false, setupAction: null,
  repositories: [
    row(),
    row({ repositoryId: 'Repositories/github/2', fullName: 'acme/gadget', managedBy: 'repo-admin', enabled: true, onboarding: 'PullRequestOpened',
      onboardingPullRequest: { number: 7, url: 'https://github.com/acme/gadget/pull/7' }, mergeQueueInFile: 'enabled' }),
    row({ repositoryId: null, fullName: 'acme/fresh', syncing: true, managedBy: 'something-new' }),
  ],
  ...over,
});

/** The words the specs read; everything else resolves like the real service does without a translation (fallback, else key). */
const WORDS: Record<string, string> = {
  'dependencyOnboardingUi.managedBy.owner': 'Organization owner',
  'dependencyOnboardingUi.managedBy.repo-admin': 'Repository admin',
  'dependencyOnboardingUi.mergeQueueInFile.enabled': 'Enabled in coverage.yml',
  'dependencyOnboardingUi.whoMayEnable': 'Only organization owners and repository admins can enable dependency updates.',
};

describe('DependencyUpdatesOnboardComponent', () => {
  async function open(url: string, options: {
    list?: () => Promise<DependencyOnboardingResponse>;
    actions?: string[];
    execute?: () => Promise<DependencyOnboardingResult | undefined>;
  } = {}) {
    const browse: BrowseServiceStub = createBrowseStub({ getDependencyOnboarding: options.list ?? (() => Promise.resolve(response())) });
    const spark = {
      getQuery: vi.fn(() => Promise.resolve({ id: 'q-onboarding', alias: ONBOARDING_QUERY, entityType: 'Repository' })),
      getEntityTypes: vi.fn(() => Promise.resolve([{ id: 'type-repository', name: 'Repository' }])),
      executeCustomAction: vi.fn(options.execute ?? (() => Promise.resolve(undefined))),
    };
    const queryActions = { actionsFor: vi.fn(() => Promise.resolve((options.actions ?? [ENABLE_ACTION]).map(name => ({ name })))) };
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(), provideNoopAnimations(),
        provideRouter([{ path: 'dependency-updates/onboard', component: DependencyUpdatesOnboardComponent }]),
        provideBrowseStub(browse),
        { provide: SparkService, useValue: spark },
        { provide: SparkQueryActionsService, useValue: queryActions },
        { provide: SparkLanguageService, useValue: { t: (key: string, fallback?: string) => WORDS[key] ?? fallback ?? key, resolve: () => '' } },
      ],
    });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url, DependencyUpdatesOnboardComponent);
    await settle(harness.fixture);
    const page = harness.fixture.debugElement.query(By.directive(DependencyUpdatesOnboardComponent)).componentInstance as DependencyUpdatesOnboardComponent;
    const el = () => harness.fixture.nativeElement as HTMLElement;
    const q = (selector: string) => el().querySelector<HTMLElement>(selector);
    return { harness, page, browse, spark, queryActions, el, q };
  }

  // R20/R32: the setup redirect's query is the forge's business on the server; the page forwards it untouched.
  it('passes the setup redirect query through untouched, and offers "all my accounts" without it', async () => {
    const { harness, browse, q } = await open('/dependency-updates/onboard?installation_id=42&setup_action=install',
      { list: () => Promise.resolve(response({ narrowed: true, setupAction: 'install' })) });

    expect(browse.getDependencyOnboarding).toHaveBeenCalledWith({ installation_id: '42', setup_action: 'install' });
    expect(q('[data-testid="onboard-narrowed"]')).not.toBeNull();
    expect(q('[data-testid="onboard-all-accounts"]')?.getAttribute('href')).toBe('/dependency-updates/onboard');

    // The same route without the query reuses the component: it must list everything again.
    await harness.navigateByUrl('/dependency-updates/onboard');
    await settle(harness.fixture);
    expect(browse.getDependencyOnboarding).toHaveBeenLastCalledWith({});
  });

  it('says an installation request waits for an organization owner', async () => {
    const { q } = await open('/dependency-updates/onboard?setup_action=request',
      { list: () => Promise.resolve(response({ setupAction: 'request', repositories: [] })) });

    expect(q('[data-testid="onboard-request"]')?.textContent).toContain('dependencyOnboardingUi.requestTitle');
  });

  it('tells a user without a linked GitHub sign-in how to link one', async () => {
    const { q } = await open('/dependency-updates/onboard', { list: () => Promise.resolve(response({ linked: false, login: null, repositories: [] })) });

    expect(q('[data-testid="onboard-not-linked"] a')?.getAttribute('href')).toBe('/account');
  });

  // R39: a user who manages nothing here sees why, not an empty table.
  it('explains who may enable when the user manages nothing in the installation', async () => {
    const { q } = await open('/dependency-updates/onboard?installation_id=99',
      { list: () => Promise.resolve(response({ narrowed: true, repositories: [] })) });

    expect(q('[data-testid="onboard-rows"]')).toBeNull();
    expect(q('[data-testid="onboard-empty"]')?.textContent).toContain('Only organization owners and repository admins');
  });

  it('shows each row\'s status: syncing, onboarding with its pull request, and coverage.yml\'s merge queue', async () => {
    const { el } = await open('/dependency-updates/onboard');
    const tr = (name: string) => el().querySelector<HTMLElement>(`tr[data-repo="${name}"]`)!;

    // Only an off repository that is in the app already can be selected.
    expect(tr('acme/widget').querySelector('[data-testid="onboard-select"]')).not.toBeNull();
    expect(tr('acme/gadget').querySelector('[data-testid="onboard-select"]')).toBeNull();
    expect(tr('acme/fresh').querySelector('[data-testid="onboard-select"]')).toBeNull();

    expect(tr('acme/fresh').querySelector('[data-testid="onboard-state"]')?.textContent).toContain('dependencyOnboardingUi.state.syncing');
    expect(tr('acme/gadget').querySelector('[data-testid="onboard-pr"]')?.getAttribute('href')).toBe('https://github.com/acme/gadget/pull/7');
    expect(tr('acme/gadget').querySelector('[data-testid="onboard-merge-queue"]')?.textContent).toContain('Enabled in coverage.yml');

    // managedBy is the server's word: translated when known, shown as sent when not.
    expect(tr('acme/widget').querySelector('[data-testid="onboard-managed-by"]')?.textContent?.trim()).toBe('Organization owner');
    expect(tr('acme/gadget').querySelector('[data-testid="onboard-managed-by"]')?.textContent?.trim()).toBe('Repository admin');
    expect(tr('acme/fresh').querySelector('[data-testid="onboard-managed-by"]')?.textContent?.trim()).toBe('something-new');
  });

  // R22: one Spark query action on the selected ids; the result is shown, refusals with their reasons.
  it('runs EnableDependencyUpdates on the selection through the Spark query, then shows what was and was not enabled', async () => {
    const result: DependencyOnboardingResult = {
      enabled: ['acme/widget'], notEnabled: [{ repositoryId: 'Repositories/github/3', fullName: 'acme/other', reason: "you don't manage this repository" }],
      withMergeQueue: true,
    };
    const { harness, page, browse, spark, q } = await open('/dependency-updates/onboard', { execute: () => Promise.resolve(result) });

    page.toggleAll(true);
    await settle(harness.fixture);
    expect((q('[data-testid="onboard-enable"]') as HTMLButtonElement).disabled).toBe(false);
    q('[data-testid="onboard-enable"]')!.click();
    await settle(harness.fixture);

    expect(spark.getQuery).toHaveBeenCalledWith(ONBOARDING_QUERY);
    expect(spark.executeCustomAction).toHaveBeenCalledWith('type-repository', ENABLE_ACTION, undefined, ['Repositories/github/1'], undefined, 'q-onboarding');
    expect(q('[data-testid="onboard-enabled"]')?.textContent).toContain('acme/widget');
    expect(q('[data-testid="onboard-not-enabled"]')?.textContent).toContain("acme/other: you don't manage this repository");
    expect(browse.getDependencyOnboarding).toHaveBeenCalledTimes(2);
    expect(page.selectedCount()).toBe(0);
  });

  it('changes nothing when the merge-queue prompt is cancelled', async () => {
    const { harness, page, browse, q } = await open('/dependency-updates/onboard', { execute: () => Promise.resolve(undefined) });

    page.toggleAll(true);
    await page.enable();
    await settle(harness.fixture);

    expect(q('[data-testid="onboard-result"]')).toBeNull();
    expect(browse.getDependencyOnboarding).toHaveBeenCalledTimes(1);
    expect(page.selectedCount()).toBe(1);
  });

  it('offers no button when the server does not offer the action to this caller', async () => {
    const { q } = await open('/dependency-updates/onboard', { actions: [] });

    expect(q('[data-testid="onboard-enable"]')).toBeNull();
    expect(q('[data-testid="onboard-select"]')).toBeNull();
    expect(q('[data-testid="onboard-cannot-enable"]')).not.toBeNull();
  });
});
