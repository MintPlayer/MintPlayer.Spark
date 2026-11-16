import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { settle } from '../../../testing/test-utils';
import ProjectBoardsSetupComponent from './project-boards-setup.component';

const STEPS = ['install', 'board', 'find', 'enable', 'rules', 'events', 'columns', 'troubleshoot'];

describe('ProjectBoardsSetupComponent', () => {
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
        provideRouter([{ path: 'project-boards/setup', component: ProjectBoardsSetupComponent }]),
      ],
    });
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url, ProjectBoardsSetupComponent);
    await settle(harness.fixture);
  }

  const el = () => harness.fixture.nativeElement as HTMLElement;
  const all = <T extends Element>(selector: string) => [...el().querySelectorAll<T>(selector)];
  const text = (testId: string) => el().querySelector(`[data-testid="${testId}"]`)?.textContent ?? '';
  const stepLinks = () => all<HTMLAnchorElement>('[data-testid="pb-setup-steps"] a');
  const rows = (testId: string) => all<HTMLTableRowElement>(`[data-testid="${testId}"] tbody tr`)
    .map(row => [...row.cells].map(cell => cell.textContent!.trim()));

  it('walks through the steps in order, each numbered and listed with a link to its anchor', async () => {
    await open('/project-boards/setup');

    const cards = all<HTMLElement>('[data-step]');
    expect(cards.map(card => card.id)).toEqual(STEPS);
    cards.forEach((card, i) => expect(card.querySelector('h2 .badge')?.textContent?.trim()).toBe(String(i + 1)));
    expect(stepLinks().map(a => a.getAttribute('href'))).toEqual(STEPS.map(id => `/project-boards/setup#${id}`));
  });

  it('scrolls to the step it is opened at, and to a step chosen from the list', async () => {
    await open('/project-boards/setup#rules');
    expect(scrolledTo).toEqual(['rules']);

    stepLinks()[STEPS.indexOf('troubleshoot')].click();
    await settle(harness.fixture);
    expect(scrolledTo).toEqual(['rules', 'troubleshoot']);
  });

  // hosting.md: the App permissions board automation needs, and that an owner has to accept them.
  it('names the App permissions and that an organization owner has to accept them', async () => {
    await open('/project-boards/setup');

    expect(text('pb-setup-permissions')).toContain('Organization projects');
    expect(text('pb-setup-accept')).toContain('an owner of the organization has to accept it');
    expect(text('pb-setup-status')).toContain('Status');
  });

  // EventColumnMapping: the rule's fields, and WebhookEventType.Items: all eighteen events, grouped.
  it('lists every rule field and all eighteen events', async () => {
    await open('/project-boards/setup');

    expect(rows('pb-setup-rule-fields').map(([field]) => field))
      .toEqual(['Event', 'Target Column', 'Enabled', 'Move Linked Issues', 'Add To Board']);
    const events = rows('pb-setup-events').flatMap(([, list]) => list.split(', '));
    expect(events).toHaveLength(18);
    expect(events).toContain('Pull request merged');
    expect(events).toContain('Pull request closed without merging');
  });

  // SyncColumnsAction: no event announces a column change, so the guide says how to pick one up.
  it('says that column changes need Sync columns, and how to tell why a card did not move', async () => {
    await open('/project-boards/setup');

    expect(text('pb-setup-enable')).toContain('Sync columns');
    expect(text('pb-setup-columns')).toContain('GitHub sends no event when you rename, add or delete a column');
    expect(text('pb-setup-rule-status')).toContain('Last Fired');
    expect(text('pb-setup-checklist')).toContain('Add To Board');
  });
});
