import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { settle } from '../../../testing/test-utils';
import BackportsSetupComponent from './backports-setup.component';

const STEPS = ['release-lines', 'ask', 'each-line', 'comment', 'by-hand', 'configure', 'protect'];

describe('BackportsSetupComponent', () => {
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
        provideRouter([{ path: 'backports/setup', component: BackportsSetupComponent }]),
      ],
    });
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url, BackportsSetupComponent);
    await settle(harness.fixture);
  }

  const el = () => harness.fixture.nativeElement as HTMLElement;
  const all = <T extends Element>(selector: string) => [...el().querySelectorAll<T>(selector)];
  const text = (testId: string) => el().querySelector(`[data-testid="${testId}"]`)?.textContent ?? '';
  const stepLinks = () => all<HTMLAnchorElement>('[data-testid="bp-setup-steps"] a');
  const rows = (testId: string) => all<HTMLTableRowElement>(`[data-testid="${testId}"] tbody tr`)
    .map(row => [...row.cells].map(cell => cell.textContent!.trim()));

  it('walks through the steps in order, each numbered and listed with a link to its anchor', async () => {
    await open('/backports/setup');

    const cards = all<HTMLElement>('[data-step]');
    expect(cards.map(card => card.id)).toEqual(STEPS);
    cards.forEach((card, i) => expect(card.querySelector('h2 .badge')?.textContent?.trim()).toBe(String(i + 1)));
    expect(stepLinks().map(a => a.getAttribute('href'))).toEqual(STEPS.map(id => `/backports/setup#${id}`));
  });

  it('scrolls to the step it is opened at, and to a step chosen from the list', async () => {
    await open('/backports/setup#configure');
    expect(scrolledTo).toEqual(['configure']);

    stepLinks()[STEPS.indexOf('by-hand')].click();
    await settle(harness.fixture);
    expect(scrolledTo).toEqual(['configure', 'by-hand']);
  });

  // #570 D5, D7: a label, a comment, or a comment asking for a draft.
  it('lists every way to ask for a backport, and the labels and check a pull request gets', async () => {
    await open('/backports/setup');

    expect(rows('bp-setup-requests').map(([how]) => how)).toEqual(['backport: 8.x', '/backport 8.x', '/backport 8.x --draft']);
    expect(text('bp-setup-default-labels')).toContain('backport: <line>');
    // #568 D13: a bot's pull request gets no labels; each line updates its own dependencies.
    expect(text('bp-setup-default-labels')).toContain('a run of its own');
    expect(text('bp-setup-targets-check')).toContain('neutral');
  });

  // BackportYml: every option, with its default, in the order of the reference.
  it('lists the backport options of coverage.yml with their defaults', async () => {
    await open('/backports/setup');

    expect(rows('bp-setup-options').map(([key, , defaultValue]) => `${key} ${defaultValue}`)).toEqual([
      'onConflict comment', 'enqueue true', 'defaultLabels all', 'fixLabels (none)', 'lines (none)', 'markers.disable (none)', 'markers.packages (none)',
    ]);
    expect(text('bp-setup-example')).toContain('packages: ["@mintplayer/ng-bootstrap", "MyCompany.*"]');
  });

  it('gives the commands for a manual backport and says what a pushed branch and a hand-made backport get', async () => {
    await open('/backports/setup');

    expect(text('bp-setup-commands')).toContain('git cherry-pick -x');
    expect(text('bp-setup-pushed-branch')).toContain('/backport <line> --draft');
    expect(text('bp-setup-backported-label')).toContain('backported: <line>');
    expect(text('bp-setup-protect')).toContain('Block force');
  });
});
