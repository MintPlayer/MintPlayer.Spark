import { Component, input, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { BsCodeSnippetComponent } from '@mintplayer/ng-bootstrap/code-snippet';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { settle } from '../../../testing/test-utils';
import DependencyUpdatesSetupComponent from './dependency-updates-setup.component';
import { DEPENDENCIES_YML, MERGE_QUEUE_ON } from './dependency-updates-setup.snippets';

/* The code snippet is a Lit element jsdom cannot host; this stand-in keeps what the page passed it. */
@Component({ selector: 'bs-code-snippet', template: '' })
class StubCodeSnippet {
  readonly code = input<string>('');
  readonly language = input<string>('');
}

const STEPS = ['prerequisites', 'enable', 'onboarding', 'coverage-yml', 'commands', 'platforms', 'security', 'troubleshooting'];

describe('DependencyUpdatesSetupComponent', () => {
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
        provideRouter([{ path: 'dependency-updates/setup', component: DependencyUpdatesSetupComponent }]),
      ],
    });
    TestBed.overrideComponent(DependencyUpdatesSetupComponent, {
      remove: { imports: [BsCodeSnippetComponent] },
      add: { imports: [StubCodeSnippet] },
    });
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url, DependencyUpdatesSetupComponent);
    await settle(harness.fixture);
  }

  const el = () => harness.fixture.nativeElement as HTMLElement;
  const all = <T extends Element>(selector: string) => [...el().querySelectorAll<T>(selector)];
  const q = (selector: string) => el().querySelector<HTMLElement>(selector);
  const stepLinks = () => all<HTMLAnchorElement>('[data-testid="du-setup-steps"] a');
  const snippets = () => harness.fixture.debugElement.queryAll(By.directive(StubCodeSnippet))
    .map(d => d.componentInstance as StubCodeSnippet);
  const rows = (testId: string) => all<HTMLTableRowElement>(`[data-testid="${testId}"] tbody tr`)
    .map(row => [...row.cells].map(cell => cell.textContent!.trim()));

  it('walks through the eight steps in order, each numbered and listed with a link to its anchor', async () => {
    await open('/dependency-updates/setup');

    const cards = all<HTMLElement>('[data-step]');
    expect(cards.map(card => card.id)).toEqual(STEPS);
    cards.forEach((card, i) => expect(card.querySelector('h2 .badge')?.textContent?.trim()).toBe(String(i + 1)));

    expect(stepLinks().map(a => a.getAttribute('href'))).toEqual(STEPS.map(id => `/dependency-updates/setup#${id}`));
    expect(stepLinks().map(a => a.textContent?.trim()))
      .toEqual(cards.map(card => card.querySelector('h2')!.textContent!.trim().replace(/^\d+/, '')));
  });

  // The router scrolls to no anchor (app.config.ts), so a deep link would land at the top without this.
  it('scrolls to the step it is opened at, and to a step chosen from the list', async () => {
    await open('/dependency-updates/setup#commands');
    expect(scrolledTo).toEqual(['commands']);

    stepLinks()[STEPS.indexOf('security')].click();
    await settle(harness.fixture);
    expect(scrolledTo).toEqual(['commands', 'security']);
  });

  // PRD D-N4: onboarding refuses to start until the installation has accepted every one of these.
  it('lists every permission and event the GitHub App needs for dependency updates', async () => {
    await open('/dependency-updates/setup');

    expect(rows('du-setup-permissions').map(([name, access]) => `${name}: ${access}`)).toEqual([
      'Contents: Read and write', 'Pull requests: Read and write', 'Checks: Read and write', 'Issues: Read and write',
      'Actions: Read and write', 'Workflows: Read and write', 'Metadata: Read-only', 'Dependabot alerts: Read-only',
    ]);
    expect(all('[data-testid="du-setup-events"] strong').map(e => e.textContent)).toEqual([
      'Workflow run', 'Check run', 'Check suite', 'Issue comment', 'Pull request', 'Push', 'Release', 'Dependabot alert',
    ]);
  });

  // R5: auto-merge is an enqueue in the merge queue, so the guide sends the reader to the queue's own setup first.
  it('makes the merge queue a prerequisite of auto-merge and links its setup guide', async () => {
    await open('/dependency-updates/setup');

    expect(q('[data-testid="du-setup-merge-queue-link"]')?.getAttribute('href')).toBe('/merge-queue/setup');
    expect(snippets().find(s => s.code() === MERGE_QUEUE_ON)?.language()).toBe('yaml');
    expect(q('[data-testid="du-setup-coverage-gate"]')?.textContent).toContain('autoMerge.maxCoverageDrop');
  });

  it('shows the dependencies: section as copyable YAML, with a row for each of its keys', async () => {
    await open('/dependency-updates/setup');

    const yml = snippets().find(s => s.code() === DEPENDENCIES_YML);
    expect(yml?.language()).toBe('yaml');
    const keys = rows('du-setup-keys').map(([key]) => key);
    for (const top of ['enabled', 'schedule', 'cooldown', 'ignore', 'exclude', 'lookupAction', 'applyAction', 'lockFileMaintenance', 'registries'])
      expect(keys).toContain(top);
    expect(keys).toEqual(expect.arrayContaining(['autoMerge.default', 'autoMerge.maxCoverageDrop', 'migrate[]', 'platforms.<sdk>']));
  });

  // R16: the reader must know the first rolling pull request waits for a person.
  it('says the first rolling pull request is a draft that never auto-merges', async () => {
    await open('/dependency-updates/setup');

    expect(q('[data-testid="du-setup-first-draft"]')?.textContent).toContain('draft and never auto-merges');
  });

  it('lists every comment command, and states the residual risk', async () => {
    await open('/dependency-updates/setup');

    expect(rows('du-setup-commands').map(([command]) => command.split(' ')[0]))
      .toEqual(['approve', 'hold', 'unhold', 'bisect', 'refresh']);
    expect(q('[data-testid="du-setup-residual-risk"]')?.textContent).toContain('with your CI\'s secrets');
  });
});
