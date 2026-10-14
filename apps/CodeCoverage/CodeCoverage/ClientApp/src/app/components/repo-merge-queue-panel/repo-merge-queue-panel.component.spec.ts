import { provideZonelessChangeDetection } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { MergeQueueEntry, MergeQueueView, MergeQueueViewer } from '../../services/browse.service';
import { BrowseServiceStub, createBrowseStub, provideBrowseStub, settle } from '../../../testing/test-utils';
import { RepoMergeQueuePanelComponent } from './repo-merge-queue-panel.component';

const anonymous: MergeQueueViewer = { signedIn: false, linked: false, login: null, role: null, credential: null };
const writer: MergeQueueViewer = { signedIn: true, linked: true, login: 'maintainer', role: 'write', credential: 'Ok' };

function entry(number: number, state: string, extra: Partial<MergeQueueEntry> = {}): MergeQueueEntry {
  return {
    number, title: `Pull request ${number}`, authorLogin: 'contributor', state, step: null, sinceUtc: '2026-10-14T09:00:00Z',
    enqueuedAtUtc: '2026-10-14T08:30:00Z', enqueuedBy: 'maintainer', enqueuedVia: 'Label', run: 1, position: null, runUrl: null, ...extra,
  };
}

function busy(viewer: MergeQueueViewer): MergeQueueView {
  return {
    supported: true,
    viewer,
    lanes: [{
      baseBranch: 'master',
      paused: false,
      current: entry(7, 'Regenerating', { position: 1, step: 'dispatched regenerate.yml run 9001', runUrl: 'https://github.com/acme/widget/actions/runs/9001' }),
      waiting: [entry(9, 'Queued', { position: 2 }), entry(8, 'Queued', { position: 3, authorLogin: 'someone' })],
      recent: [
        { number: 6, title: 'Pull request 6', run: 1, state: 'Evicted', reason: 'author-push', message: 'The branch moved.', enqueuedBy: 'maintainer', mergeCommitSha: null, finishedAtUtc: '2026-10-14T08:02:00Z' },
        { number: 5, title: 'Pull request 5', run: 1, state: 'Merged', reason: null, message: null, enqueuedBy: 'maintainer', mergeCommitSha: 'feedface', finishedAtUtc: '2026-10-14T08:01:00Z' },
      ],
    }],
    config: {
      number: 7, run: 1, configRef: 'master',
      values: [{ key: 'approvalFloor', value: 'ExternalAuthors', source: 'default' }, { key: 'postRebaseActionName', value: 'regenerate.yml', source: 'coverage.yml' }],
    },
  };
}

const idle = (viewer: MergeQueueViewer): MergeQueueView => ({ supported: true, viewer, lanes: [], config: null });

describe('RepoMergeQueuePanelComponent', () => {
  let fixture: ComponentFixture<RepoMergeQueuePanelComponent>;
  let browse: BrowseServiceStub;

  async function create(getMergeQueue: () => Promise<MergeQueueView>, overrides: Partial<Record<keyof BrowseServiceStub, (...args: any[]) => any>> = {}, provider = 'github') {
    browse = createBrowseStub({ getMergeQueue, ...overrides });
    TestBed.configureTestingModule({
      imports: [RepoMergeQueuePanelComponent],
      providers: [provideZonelessChangeDetection(), provideNoopAnimations(), provideRouter([]), provideBrowseStub(browse)],
    });
    fixture = TestBed.createComponent(RepoMergeQueuePanelComponent);
    fixture.componentRef.setInput('provider', provider);
    fixture.componentRef.setInput('owner', 'acme');
    fixture.componentRef.setInput('name', 'widget');
    await settle(fixture);
  }

  const el = () => fixture.nativeElement as HTMLElement;
  const q = (selector: string) => el().querySelector(selector) as HTMLElement | null;
  const buttons = (text: string) => [...el().querySelectorAll('button')].filter(b => b.textContent?.includes(text));

  it('shows the running entry, the waiting ones in order, how the latest runs ended and the configuration a run used', async () => {
    await create(() => Promise.resolve(busy(anonymous)));

    expect(browse.getMergeQueue).toHaveBeenCalledWith('github', 'acme', 'widget');
    const current = q('[data-testid="mq-current"]')!;
    expect(current.textContent).toContain('#7');
    expect(current.textContent).toContain('regenerating');
    expect(current.querySelector('a')?.getAttribute('href')).toBe('https://github.com/acme/widget/actions/runs/9001');
    expect(el().textContent).toContain('dispatched regenerate.yml run 9001');

    const waiting = [...el().querySelectorAll('[data-testid="mq-waiting"] li')].map(li => li.querySelector('strong')?.textContent);
    expect(waiting).toEqual(['#9', '#8']);

    const recent = [...el().querySelectorAll('[data-testid="mq-recent"] tbody tr')].map(tr => tr.textContent ?? '');
    expect(recent[0]).toContain('#6');
    expect(recent[0]).toContain('evicted');
    expect(recent[0]).toContain('author-push');
    expect(recent[1]).toContain('merged');

    const config = q('[data-testid="mq-config"]')!;
    expect(config.textContent).toContain('the run of #7');
    expect(config.textContent).toContain('approvalFloor');
    expect(config.textContent).toContain('coverage.yml');

    // An anonymous visitor reads the queue; there is nothing for them to press.
    expect(q('[data-testid="mq-enqueue"]')).toBeNull();
    expect(buttons('Dequeue')).toHaveLength(0);
    expect(q('[data-testid="mq-hint"]')).toBeNull();
  });

  // A state must read in both themes (merge-queue PRD §0 D75, D84). Bootstrap's *-subtle backgrounds with *-emphasis text
  // are its own pairs for both themes: 7.15:1 or more, where <bs-badge>'s white text gets 1.96:1 on info and 1.63:1 on
  // warning. The .mq-state class is the chip's shape, since .badge is styled only inside <bs-badge>.
  it('shows each state as a chip, a subtle background with emphasis text', async () => {
    const view = busy(anonymous);
    await create(() => Promise.resolve({ ...view, lanes: [{ ...view.lanes[0], paused: true }] }));

    const states = [...el().querySelectorAll('[data-testid="mq-state"]')]
      .map(s => [s.textContent?.trim(), [...s.classList].filter(c => c !== 'ms-1').sort().join(' ')]);
    expect(states).toEqual([
      ['paused', 'bg-secondary-subtle border border-secondary-subtle mq-state text-secondary-emphasis'],
      ['regenerating', 'bg-info-subtle border border-info-subtle mq-state text-info-emphasis'],
      ['evicted', 'bg-danger-subtle border border-danger-subtle mq-state text-danger-emphasis'],
      ['merged', 'bg-success-subtle border border-success-subtle mq-state text-success-emphasis'],
    ]);
  });

  // For the repository's account, so the guide can show that account's pending-permissions notice.
  it('links the GitHub setup guide for the repository\'s account, and only on GitHub', async () => {
    await create(() => Promise.resolve(busy(anonymous)));
    expect(q('[data-testid="mq-setup-link"]')?.getAttribute('href')).toBe('/merge-queue/setup?provider=github&account=acme');

    TestBed.resetTestingModule();
    await create(() => Promise.resolve(busy(anonymous)), {}, 'gitlab');
    expect(q('[data-testid="merge-queue"]')).not.toBeNull();
    expect(q('[data-testid="mq-setup-link"]')).toBeNull();
  });

  it('is hidden for a forge without a queue, when the server refuses, and for an anonymous visitor of an unused queue', async () => {
    await create(() => Promise.resolve({ ...busy(writer), supported: false }));
    expect(q('[data-testid="merge-queue"]')).toBeNull();

    TestBed.resetTestingModule();
    await create(() => Promise.reject(new HttpErrorResponse({ status: 404, error: { error: 'Not found' } })));
    expect(q('[data-testid="merge-queue"]')).toBeNull();

    TestBed.resetTestingModule();
    await create(() => Promise.resolve(idle(anonymous)));
    expect(q('[data-testid="merge-queue"]')).toBeNull();
  });

  it('queues a pull request as the signed-in user and reports what the server accepted', async () => {
    await create(() => Promise.resolve(idle(writer)), {
      enqueuePullRequest: () => Promise.resolve({ number: 12, command: 'enqueue', actor: 'maintainer' }),
    });
    expect(el().textContent).toContain('Nothing has gone through the queue yet');
    expect(buttons('Queue as @maintainer')).toHaveLength(1);

    fixture.componentInstance.number.set(12);
    buttons('Queue as')[0].click();
    await settle(fixture);

    expect(browse.enqueuePullRequest).toHaveBeenCalledWith('github', 'acme', 'widget', 12);
    expect(q('[data-testid="mq-notice"]')?.textContent).toContain('Asked the queue to take #12 as @maintainer');
    expect(browse.getMergeQueue).toHaveBeenCalledTimes(2);
    expect(fixture.componentInstance.number()).toBeNull();
  });

  it('submits on Enter without letting the form navigate away', async () => {
    await create(() => Promise.resolve(idle(writer)), {
      enqueuePullRequest: () => Promise.resolve({ number: 3, command: 'enqueue', actor: 'maintainer' }),
    });
    fixture.componentInstance.number.set(3);

    const submit = new Event('submit', { cancelable: true });
    q('[data-testid="mq-enqueue"] form')!.dispatchEvent(submit);
    await settle(fixture);

    expect(submit.defaultPrevented).toBe(true);
    expect(browse.enqueuePullRequest).toHaveBeenCalledWith('github', 'acme', 'widget', 3);
  });

  it('shows the server\'s own reason when the button is refused, and asks for a number before sending anything', async () => {
    await create(() => Promise.resolve(idle(writer)), {
      enqueuePullRequest: () => Promise.reject(new HttpErrorResponse({ status: 422, error: { error: 'acme/widget has no pull request #404.' } })),
    });

    buttons('Queue as')[0].click();
    await settle(fixture);
    expect(q('[data-testid="mq-notice"]')?.textContent).toContain('Enter the number of an open pull request.');
    expect(browse.enqueuePullRequest).not.toHaveBeenCalled();

    fixture.componentInstance.number.set(404);
    buttons('Queue as')[0].click();
    await settle(fixture);
    expect(q('[data-testid="mq-notice"]')?.textContent).toContain('acme/widget has no pull request #404.');
  });

  it('dequeues an entry, and offers that to an author without write access only for their own pull request', async () => {
    await create(() => Promise.resolve(busy({ ...writer, login: 'someone', role: 'read' })), {
      dequeuePullRequest: () => Promise.resolve({ number: 8, command: 'dequeue', actor: 'someone' }),
    });

    // #8 is someone's own; #7 and #9 are not, and read access may not queue.
    expect(buttons('Dequeue')).toHaveLength(1);
    expect(q('[data-testid="mq-enqueue"]')).toBeNull();
    expect(q('[data-testid="mq-hint"]')?.textContent).toContain('needs write access to acme/widget on GitHub, and you have read access');

    buttons('Dequeue')[0].click();
    await settle(fixture);
    expect(browse.dequeuePullRequest).toHaveBeenCalledWith('github', 'acme', 'widget', 8);
    expect(q('[data-testid="mq-notice"]')?.textContent).toContain('let #8 go');
  });

  it('tells a signed-in user without a usable GitHub sign-in why there is no button', async () => {
    await create(() => Promise.resolve(idle({ signedIn: true, linked: false, login: null, role: null, credential: null })));
    expect(q('[data-testid="mq-hint"]')?.textContent).toContain('no linked GitHub sign-in');

    TestBed.resetTestingModule();
    await create(() => Promise.resolve(idle({ signedIn: true, linked: true, login: null, role: null, credential: 'ReauthRequired' })));
    expect(q('[data-testid="mq-hint"]')?.textContent).toContain('has expired');
    expect(q('[data-testid="mq-enqueue"]')).toBeNull();
  });

  it('offers the buttons when the role could not be read, and leaves the decision to the queue', async () => {
    await create(() => Promise.resolve(busy({ ...writer, role: null })));

    expect(buttons('Dequeue')).toHaveLength(3);
    expect(q('[data-testid="mq-enqueue"]')).not.toBeNull();
  });
});
