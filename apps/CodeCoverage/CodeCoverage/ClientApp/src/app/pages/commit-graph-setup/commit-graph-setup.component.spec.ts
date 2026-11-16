import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { settle } from '../../../testing/test-utils';
import CommitGraphSetupComponent from './commit-graph-setup.component';

const STEPS = ['where', 'reading', 'tools', 'releases', 'current', 'unavailable', 'privacy'];

describe('CommitGraphSetupComponent', () => {
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
        provideRouter([{ path: 'commit-graph/setup', component: CommitGraphSetupComponent }]),
      ],
    });
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url, CommitGraphSetupComponent);
    await settle(harness.fixture);
  }

  const el = () => harness.fixture.nativeElement as HTMLElement;
  const all = <T extends Element>(selector: string) => [...el().querySelectorAll<T>(selector)];
  const text = (testId: string) => el().querySelector(`[data-testid="${testId}"]`)?.textContent ?? '';
  const stepLinks = () => all<HTMLAnchorElement>('[data-testid="cg-setup-steps"] a');
  const rows = (testId: string) => all<HTMLTableRowElement>(`[data-testid="${testId}"] tbody tr`)
    .map(row => [...row.cells].map(cell => cell.textContent!.trim()));

  it('walks through the steps in order, each numbered and listed with a link to its anchor', async () => {
    await open('/commit-graph/setup');

    const cards = all<HTMLElement>('[data-step]');
    expect(cards.map(card => card.id)).toEqual(STEPS);
    cards.forEach((card, i) => expect(card.querySelector('h2 .badge')?.textContent?.trim()).toBe(String(i + 1)));
    expect(stepLinks().map(a => a.getAttribute('href'))).toEqual(STEPS.map(id => `/commit-graph/setup#${id}`));
  });

  it('scrolls to the step it is opened at, and to a step chosen from the list', async () => {
    await open('/commit-graph/setup#unavailable');
    expect(scrolledTo).toEqual(['unavailable']);

    stepLinks()[STEPS.indexOf('privacy')].click();
    await settle(harness.fixture);
    expect(scrolledTo).toEqual(['unavailable', 'privacy']);
  });

  it('links to the accounts list, where the repository pages are', async () => {
    await open('/commit-graph/setup');

    expect(el().querySelector('[data-testid="cg-setup-accounts-link"]')?.getAttribute('href')).toBe('/po/forge-accounts/github');
  });

  // commit-graph.md §1 (node encoding) and §4 (the default quotas and every `unavailable` reason).
  it('explains the node encoding, the limits and every reason a graph is unavailable', async () => {
    await open('/commit-graph/setup');

    expect(rows('cg-setup-nodes').map(([node]) => node)).toEqual(['Filled', 'Ring', 'Hollow grey']);
    expect(rows('cg-setup-limits').map(([, size, refs]) => [size, refs])).toEqual([['2 GB', '50,000'], ['500 MB', '10,000']]);
    expect(rows('cg-setup-reasons').map(([reason]) => reason))
      .toEqual(['too-many-refs', 'too-large', 'timeout', 'disk-floor', 'auth', 'not-found', 'error']);
    expect(text('cg-setup-deletion')).toContain('deleted when the access goes');
  });
});
