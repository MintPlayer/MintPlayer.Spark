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
import { MERGE_QUEUE_SETUP_SCREENSHOTS, type MergeQueueSetupScreenshot } from './merge-queue-setup.screenshots';

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

  describe('screenshots', () => {
    // The page reads MERGE_QUEUE_SETUP_SCREENSHOTS when it renders, so each case sets the URLs it needs there and the
    // committed ones come back afterwards: no case depends on which screenshots have been uploaded so far.
    const names = Object.keys(MERGE_QUEUE_SETUP_SCREENSHOTS) as MergeQueueSetupScreenshot[];
    const committed = { ...MERGE_QUEUE_SETUP_SCREENSHOTS };
    afterEach(() => { Object.assign(MERGE_QUEUE_SETUP_SCREENSHOTS, committed); });

    /** A stand-in for the address GitHub gives an uploaded image, one per screenshot. */
    const attachmentUrl = (name: MergeQueueSetupScreenshot) =>
      `https://github.com/user-attachments/assets/00000000-0000-4000-8000-${String(names.indexOf(name)).padStart(12, '0')}`;
    const setUrls = (url: (name: MergeQueueSetupScreenshot) => string | undefined) =>
      names.forEach(name => { MERGE_QUEUE_SETUP_SCREENSHOTS[name] = url(name); });
    const figures = () => all<HTMLElement>('figure[data-shot]');
    const shotName = (figure: Element) => figure.getAttribute('data-shot') as MergeQueueSetupScreenshot;

    it('shows each one once, and a "Screenshot pending" box, without requesting anything, for those without a URL', async () => {
      setUrls(() => undefined);
      await open('/merge-queue/setup');

      expect(figures().map(shotName).sort()).toEqual([...names].sort());
      expect(all('img')).toHaveLength(0);
      for (const figure of figures()) {
        const pending = figure.querySelector('[data-shot-pending]')!;
        expect(pending.classList).toContain('shot-pending');
        expect(pending.getAttribute('role')).toBe('img');
        expect(pending.getAttribute('aria-label')!.length).toBeGreaterThan(40);
        expect(pending.textContent).toContain('Screenshot pending');
        expect(figure.querySelector('figcaption')?.textContent?.trim()).not.toBe('');
      }
      const descriptions = figures().map(figure => figure.querySelector('[data-shot-pending]')!.getAttribute('aria-label'));
      expect(new Set(descriptions).size).toBe(names.length);
    });

    it('loads one with a URL from that URL, lazily, at its declared size and with its description', async () => {
      setUrls(attachmentUrl);
      await open('/merge-queue/setup');

      expect(figures()).toHaveLength(names.length);
      for (const figure of figures()) {
        const image = figure.querySelector('img')!;
        expect(image.getAttribute('src')).toBe(attachmentUrl(shotName(figure)));
        expect(image.getAttribute('loading')).toBe('lazy');
        expect(image.getAttribute('width')).toBe('1280');
        expect(image.getAttribute('height')).toBe('720');
        expect(image.alt.length).toBeGreaterThan(40);
        expect(figure.querySelector('[data-shot-pending]')).toBeNull();
      }
      expect(new Set(figures().map(figure => figure.querySelector('img')!.alt)).size).toBe(names.length);
    });

    it('shows the "Screenshot pending" box, with the same description, for an image that fails to load', async () => {
      setUrls(attachmentUrl);
      await open('/merge-queue/setup');
      const figure = el().querySelector('[data-shot="ruleset-1-bypass"]')!;
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
      expect(all('figure img')).toHaveLength(names.length - 1);
    });

    // The address a rendered attachment is served from can carry a token that expires; only this form lasts.
    it('holds every committed URL to the form GitHub gives an attachment', () => {
      const attachment = /^https:\/\/github\.com\/user-attachments\/assets\/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
      expect(attachmentUrl('ruleset-1-bypass')).toMatch(attachment);
      expect('https://private-user-images.githubusercontent.com/1/2-3.png?jwt=x').not.toMatch(attachment);

      for (const name of names) {
        if (committed[name] !== undefined) expect(committed[name], name).toMatch(attachment);
      }
    });
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
