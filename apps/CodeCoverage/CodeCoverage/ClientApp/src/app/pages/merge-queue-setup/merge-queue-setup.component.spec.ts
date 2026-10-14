import { Component, input, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { BsCodeSnippetComponent } from '@mintplayer/ng-bootstrap/code-snippet';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { PendingInstallationPermissions } from '../../services/browse.service';
import { BrowseServiceStub, createBrowseStub, provideBrowseStub, settle } from '../../../testing/test-utils';
import MergeQueueSetupComponent from './merge-queue-setup.component';

/* The code snippet is a Lit element jsdom cannot host; this stand-in keeps what the page passed it. */
@Component({ selector: 'bs-code-snippet', template: '' })
class StubCodeSnippet {
  readonly code = input<string>('');
  readonly language = input<string>('');
}

const STEPS = ['permissions', 'native-queue', 'ruleset-1', 'ruleset-2', 'coverage-yml', 'regenerate', 'forks', 'settings'];

describe('MergeQueueSetupComponent', () => {
  let harness: RouterTestingHarness;
  let browse: BrowseServiceStub;
  let scrolledTo: string[];
  const scrollIntoView = Element.prototype.scrollIntoView;

  // jsdom has no scrollIntoView: record which element the page scrolled to instead.
  beforeEach(() => {
    scrolledTo = [];
    Element.prototype.scrollIntoView = function (this: Element) { scrolledTo.push(this.id); };
  });
  afterEach(() => { Element.prototype.scrollIntoView = scrollIntoView; });

  async function open(url: string, getPendingPermissions: () => Promise<PendingInstallationPermissions | null> = () => Promise.resolve(null)) {
    browse = createBrowseStub({ getPendingPermissions });
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(), provideNoopAnimations(), provideBrowseStub(browse),
        provideRouter([{ path: 'merge-queue/setup', component: MergeQueueSetupComponent }]),
      ],
    });
    TestBed.overrideComponent(MergeQueueSetupComponent, {
      remove: { imports: [BsCodeSnippetComponent] },
      add: { imports: [StubCodeSnippet] },
    });
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url, MergeQueueSetupComponent);
    await settle(harness.fixture);
  }

  const el = () => harness.fixture.nativeElement as HTMLElement;
  const all = <T extends Element>(selector: string) => [...el().querySelectorAll<T>(selector)];
  const stepLinks = () => all<HTMLAnchorElement>('[data-testid="mq-setup-steps"] a');
  const snippet = (marker: string) => harness.fixture.debugElement.queryAll(By.directive(StubCodeSnippet))
    .map(d => d.componentInstance as StubCodeSnippet)
    .find(s => s.code().includes(marker))!;
  /** A file without its comment lines: what the workflow actually does. */
  const live = (code: string) => code.split('\n').filter(line => !line.trim().startsWith('#')).join('\n');

  it('walks through the eight steps in order, each numbered and listed with a link to its anchor', async () => {
    await open('/merge-queue/setup');

    const cards = all<HTMLElement>('[data-step]');
    expect(cards.map(card => card.id)).toEqual(STEPS);
    cards.forEach((card, i) => expect(card.querySelector('h2 .badge')?.textContent?.trim()).toBe(String(i + 1)));

    expect(stepLinks().map(a => a.getAttribute('href'))).toEqual(STEPS.map(id => `/merge-queue/setup#${id}`));
    expect(stepLinks().map(a => a.textContent?.trim()))
      .toEqual(cards.map(card => card.querySelector('h2')!.textContent!.trim().replace(/^\d+/, '')));
  });

  // The router scrolls to no anchor (app.config.ts), so a message's deep link would land at the top without this.
  it('scrolls to the step it is opened at, and to a step chosen from the list', async () => {
    await open('/merge-queue/setup#ruleset-2');
    expect(scrolledTo).toEqual(['ruleset-2']);

    stepLinks()[STEPS.indexOf('regenerate')].click();
    await settle(harness.fixture);
    expect(scrolledTo).toEqual(['ruleset-2', 'regenerate']);
  });

  it('declares every screenshot\'s size, loads it lazily and describes it', async () => {
    await open('/merge-queue/setup');

    const images = all<HTMLImageElement>('figure img');
    expect(images).toHaveLength(9);
    for (const image of images) {
      expect(image.getAttribute('loading')).toBe('lazy');
      expect(image.getAttribute('width')).toBe('1280');
      expect(image.getAttribute('height')).toBe('720');
      expect(image.getAttribute('src')).toMatch(/^merge-queue-setup\/[a-z0-9-]+\.png$/);
      expect(image.alt.length).toBeGreaterThan(40);
    }
    expect(new Set(images.map(image => image.alt)).size).toBe(images.length);
    expect(new Set(images.map(image => image.getAttribute('src'))).size).toBe(images.length);
  });

  it('shows a "Screenshot pending" box with the same description for an image that fails to load', async () => {
    await open('/merge-queue/setup');
    const figure = el().querySelector('[data-shot="ruleset-1-bypass.png"]')!;
    const alt = figure.querySelector('img')!.alt;

    figure.querySelector('img')!.dispatchEvent(new Event('error'));
    await settle(harness.fixture);

    const pending = figure.querySelector('[data-shot-pending]')!;
    expect(figure.querySelector('img')).toBeNull();
    expect(pending.classList).toContain('shot-pending');
    expect(pending.getAttribute('role')).toBe('img');
    expect(pending.getAttribute('aria-label')).toBe(alt);
    expect(pending.textContent).toContain('Screenshot pending');
    expect(figure.querySelector('figcaption')?.textContent).toContain('Ruleset 1');
    // Only that one: the others still load.
    expect(all('figure img')).toHaveLength(8);
  });

  it('shows the coverage.yml section and a regenerate workflow that keeps the rules the queue relies on', async () => {
    await open('/merge-queue/setup');

    const config = snippet('mergeQueue:');
    expect(config.language()).toBe('yaml');
    expect(config.code()).toContain('enabled: true');
    expect(config.code()).toContain('postRebaseActionName: regenerate.yml');

    const workflow = snippet('workflow_dispatch:');
    expect(workflow.language()).toBe('yaml');
    for (const rule of ['contents: read', 'persist-credentials: false', 'ref: ${{ github.sha }}', 'runs-on: ubuntu-latest', '/spark-regenerate@<commit-sha>']) {
      expect(live(workflow.code())).toContain(rule);
    }
    for (const forbidden of ['secrets.', 'id-token', 'self-hosted', 'environment:', '@master']) {
      expect(live(workflow.code())).not.toContain(forbidden);
    }

    expect(snippet('branches-ignore').code()).toContain('branches-ignore: [\'mq/**\']');
  });

  it('gives ruleset 1 the App exempt and admins pull requests only, and ruleset 2 both approval settings off', async () => {
    await open('/merge-queue/setup');

    const ruleset1 = snippet('"type": "update"').code();
    expect(ruleset1).toContain('{ "actor_id": <APP_ID>, "actor_type": "Integration", "bypass_mode": "exempt" }');
    expect(ruleset1).toContain('{ "actor_id": 5, "actor_type": "RepositoryRole", "bypass_mode": "pull_request" }');

    const ruleset2 = snippet('"required_status_checks"').code();
    expect(ruleset2).toContain('"dismiss_stale_reviews_on_push": false');
    expect(ruleset2).toContain('"require_last_push_approval": false');
    expect(ruleset2).toContain('"bypass_actors": []');
    expect(ruleset2).not.toContain('Merge queue');
  });

  it('opened for an account, shows its pending permissions in step 1 and links its organization page in step 8', async () => {
    await open('/merge-queue/setup?provider=github&account=acme', () => Promise.resolve({
      htmlUrl: 'https://github.com/organizations/acme/settings/installations/42',
      pending: [{ name: 'actions', displayName: 'Actions', declared: 'write', accepted: null }],
    }));

    expect(browse.getPendingPermissions).toHaveBeenCalledWith('github', 'acme');
    const notice = el().querySelector('#permissions [data-testid="pending-permissions"]')!;
    expect(notice.textContent).toContain('Actions (write)');
    expect(notice.querySelector('a')?.getAttribute('href')).toBe('https://github.com/organizations/acme/settings/installations/42');
    // The notice's own link to this guide would point at the page it is on.
    expect(notice.querySelector('[data-testid="merge-queue-setup-link"]')).toBeNull();

    expect(el().querySelector('#settings [data-testid="mq-setup-account-link"]')?.getAttribute('href')).toBe('/github/a/acme');
    // Jumping between steps keeps the account.
    expect(stepLinks()[0].getAttribute('href')).toBe('/merge-queue/setup?provider=github&account=acme#permissions');
  });

  it('opened without an account, asks the server nothing', async () => {
    await open('/merge-queue/setup');

    expect(browse.getPendingPermissions).not.toHaveBeenCalled();
    expect(el().querySelector('[data-testid="pending-permissions"]')).toBeNull();
    expect(el().querySelector('[data-testid="mq-setup-account-link"]')).toBeNull();
  });
});
