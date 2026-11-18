import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { settle } from '../../../testing/test-utils';
import VersioningSetupComponent from './versioning-setup.component';

const STEPS = ['how', 'onboard', 'workflow', 'configure', 'releases', 'troubleshoot'];

describe('VersioningSetupComponent', () => {
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
        provideRouter([{ path: 'versioning/setup', component: VersioningSetupComponent }]),
      ],
    });
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url, VersioningSetupComponent);
    await settle(harness.fixture);
  }

  const el = () => harness.fixture.nativeElement as HTMLElement;
  const all = <T extends Element>(selector: string) => [...el().querySelectorAll<T>(selector)];
  const text = (testId: string) => el().querySelector(`[data-testid="${testId}"]`)?.textContent ?? '';
  const href = (testId: string) => el().querySelector(`[data-testid="${testId}"]`)?.getAttribute('href');
  const stepLinks = () => all<HTMLAnchorElement>('[data-testid="vs-setup-steps"] a');
  const rows = (testId: string) => all<HTMLTableRowElement>(`[data-testid="${testId}"] tbody tr`)
    .map(row => [...row.cells].map(cell => cell.textContent!.trim()));

  it('walks through the steps in order, each numbered and listed with a link to its anchor', async () => {
    await open('/versioning/setup');

    const cards = all<HTMLElement>('[data-step]');
    expect(cards.map(card => card.id)).toEqual(STEPS);
    cards.forEach((card, i) => expect(card.querySelector('h2 .badge')?.textContent?.trim()).toBe(String(i + 1)));
    expect(stepLinks().map(a => a.getAttribute('href'))).toEqual(STEPS.map(id => `/versioning/setup#${id}`));
  });

  it('scrolls to the step it is opened at, and to a step chosen from the list', async () => {
    await open('/versioning/setup#workflow');
    expect(scrolledTo).toEqual(['workflow']);

    stepLinks()[STEPS.indexOf('troubleshoot')].click();
    await settle(harness.fixture);
    expect(scrolledTo).toEqual(['workflow', 'troubleshoot']);
  });

  it('links to the accounts list and the backports guide', async () => {
    await open('/versioning/setup');

    expect(href('vs-setup-accounts-link')).toBe('/po/forge-accounts/github');
    expect(href('vs-setup-backports-link')).toBe('/backports/setup');
  });

  // versioning.md: the step runs after the tests with the permissions it needs, and the coverage.yml keys.
  it('shows the workflow step with its permissions, and the coverage.yml keys', async () => {
    await open('/versioning/setup');

    const workflow = text('vs-setup-workflow');
    expect(workflow).toContain('contents: write');
    expect(workflow).toContain('id-token: write');
    expect(workflow).toContain('versioning-action@versioning-v1');
    expect(workflow.indexOf('dotnet test')).toBeLessThan(workflow.indexOf('versioning-action'));
    expect(rows('vs-setup-keys').map(([key]) => key)).toContain('prerelease');
    expect(rows('vs-setup-keys').map(([key]) => key)).toContain('lines');
    expect(text('vs-setup-no-release-trigger')).toContain('per GitHub Release');
  });
});
