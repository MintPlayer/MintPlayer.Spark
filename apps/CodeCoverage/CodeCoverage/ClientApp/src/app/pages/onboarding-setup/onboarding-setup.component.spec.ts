import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { settle } from '../../../testing/test-utils';
import OnboardingSetupComponent from './onboarding-setup.component';

const STEPS = ['choose', 'features', 'pull-request', 'on-merge', 'again', 'by-hand'];

describe('OnboardingSetupComponent', () => {
  let harness: RouterTestingHarness;
  let scrolledTo: string[];
  const scrollIntoView = Element.prototype.scrollIntoView;

  // jsdom has no scrollIntoView: record which element the page scrolled to instead.
  beforeEach(() => {
    scrolledTo = [];
    Element.prototype.scrollIntoView = function (this: Element) { scrolledTo.push(this.id); };
  });
  afterEach(() => { Element.prototype.scrollIntoView = scrollIntoView; });

  async function open(url: string) {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(), provideNoopAnimations(),
        provideRouter([{ path: 'onboarding/setup', component: OnboardingSetupComponent }]),
      ],
    });
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url, OnboardingSetupComponent);
    await settle(harness.fixture);
  }

  const el = () => harness.fixture.nativeElement as HTMLElement;
  const all = <T extends Element>(selector: string) => [...el().querySelectorAll<T>(selector)];
  const text = (testId: string) => el().querySelector(`[data-testid="${testId}"]`)?.textContent ?? '';
  const stepLinks = () => all<HTMLAnchorElement>('[data-testid="ob-setup-steps"] a');

  it('walks through the steps in order, each numbered and listed with a link to its anchor', async () => {
    await open('/onboarding/setup');

    const cards = all<HTMLElement>('[data-step]');
    expect(cards.map(card => card.id)).toEqual(STEPS);
    cards.forEach((card, i) => expect(card.querySelector('h2 .badge')?.textContent?.trim()).toBe(String(i + 1)));
    expect(stepLinks().map(a => a.getAttribute('href'))).toEqual(STEPS.map(id => `/onboarding/setup#${id}`));
  });

  it('scrolls to the step it is opened at, and to a step chosen from the list', async () => {
    await open('/onboarding/setup#on-merge');
    expect(scrolledTo).toEqual(['on-merge']);

    stepLinks()[STEPS.indexOf('again')].click();
    await settle(harness.fixture);
    expect(scrolledTo).toEqual(['on-merge', 'again']);
  });

  // The chooser's form (SetUpFeatures), in its order; each feature links to its own guide where the app has one.
  it('lists every feature of the chooser with its own guide', async () => {
    await open('/onboarding/setup');

    const rows = all<HTMLTableRowElement>('[data-testid="ob-setup-features"] tbody tr');
    expect(rows.map(row => row.cells[0].textContent!.trim())).toEqual(
      ['Code coverage', 'Dependency updates', 'Merge queue', 'Package versioning', 'Release lines', 'Size labels']);
    expect(rows.map(row => row.cells[0].querySelector('a')?.getAttribute('href') ?? null)).toEqual(
      ['/coverage/setup', '/dependency-updates/setup', '/merge-queue/setup', null, '/backports/setup', '/pr-size-labels/setup']);
  });

  // #571 D2, D3, D9 and M10d: rebuilt on every head; seeds, settings, renames and line workflows on merge.
  it('says the pull request is rebuilt and what happens when it merges', async () => {
    await open('/onboarding/setup');

    expect(text('ob-setup-rebuild')).toContain("Don't push to mintplayer/onboarding");
    expect(text('ob-setup-coverage-yml')).toContain('alphabetical order');
    expect(all('[data-testid="ob-setup-on-merge"] li')).toHaveLength(4);
    expect(text('ob-setup-on-merge')).toContain('mintplayer/onboarding-<line>');
    expect(text('ob-setup-settings-only')).toContain('right away');
    expect(text('ob-setup-by-hand')).toContain('rulesets');
  });
});
