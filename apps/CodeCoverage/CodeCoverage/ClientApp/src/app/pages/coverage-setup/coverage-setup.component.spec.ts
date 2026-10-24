import { Component, Directive, inject, input, provideZonelessChangeDetection, TemplateRef, ViewContainerRef } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { BsCodeSnippetComponent } from '@mintplayer/ng-bootstrap/code-snippet';
import { BsTabControlComponent, BsTabPageComponent, BsTabPageHeaderDirective } from '@mintplayer/ng-bootstrap/tab-control';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { settle } from '../../../testing/test-utils';
import CoverageSetupComponent from './coverage-setup.component';

/*
 * The tab control and the code snippet are Lit elements jsdom cannot host. These stand-ins render every page and header
 * inline, so a spec sees all examples at once, and keep what the page passed the snippets.
 */
@Component({ selector: 'bs-tab-control', template: '<ng-content />' })
class StubTabControl {
  readonly border = input<boolean>(false);
}

@Component({ selector: 'bs-tab-page', template: '<ng-content />' })
class StubTabPage {}

@Directive({ selector: '[bsTabPageHeader]' })
class StubTabPageHeader {
  constructor() {
    inject(ViewContainerRef).createEmbeddedView(inject(TemplateRef));
  }
}

@Component({ selector: 'bs-code-snippet', template: '' })
class StubCodeSnippet {
  readonly code = input<string>('');
  readonly language = input<string>('');
}

const STEPS = ['install', 'workflow', 'private', 'results'];

describe('CoverageSetupComponent', () => {
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
        provideRouter([{ path: 'coverage/setup', component: CoverageSetupComponent }]),
      ],
    });
    TestBed.overrideComponent(CoverageSetupComponent, {
      remove: { imports: [BsTabControlComponent, BsTabPageComponent, BsTabPageHeaderDirective, BsCodeSnippetComponent] },
      add: { imports: [StubTabControl, StubTabPage, StubTabPageHeader, StubCodeSnippet] },
    });
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url, CoverageSetupComponent);
    await settle(harness.fixture);
  }

  const el = () => harness.fixture.nativeElement as HTMLElement;
  const all = <T extends Element>(selector: string) => [...el().querySelectorAll<T>(selector)];
  const stepLinks = () => all<HTMLAnchorElement>('[data-testid="cov-setup-steps"] a');
  const snippets = () => harness.fixture.debugElement.queryAll(By.directive(StubCodeSnippet))
    .map(d => d.componentInstance as StubCodeSnippet);

  it('walks through the steps in order, each numbered and listed with a link to its anchor', async () => {
    await open('/coverage/setup');

    const cards = all<HTMLElement>('[data-step]');
    expect(cards.map(card => card.id)).toEqual(STEPS);
    cards.forEach((card, i) => expect(card.querySelector('h2 .badge')?.textContent?.trim()).toBe(String(i + 1)));
    expect(stepLinks().map(a => a.getAttribute('href'))).toEqual(STEPS.map(id => `/coverage/setup#${id}`));
  });

  it('scrolls to the step it is opened at, and to a step chosen from the list', async () => {
    await open('/coverage/setup#private');
    expect(scrolledTo).toEqual(['private']);

    stepLinks()[STEPS.indexOf('results')].click();
    await settle(harness.fixture);
    expect(scrolledTo).toEqual(['private', 'results']);
  });

  // One forge configured: the per-language tabs render directly, with the intro above them.
  it('shows the GitHub intro and every ecosystem, without a one-item forge tab', async () => {
    await open('/coverage/setup');

    expect(el().querySelector('[data-testid="cov-setup-intro"]')!.innerHTML).toContain('<code>.github/workflows/ci.yml</code>');
    expect(el().textContent).not.toContain('GitHub Actions');
    for (const label of ['.NET', 'Node.js', 'Angular', 'React', 'Python', 'Java', 'Nx']) expect(el().textContent).toContain(label);
  });

  it('hands out the workflows against this deployment, and the Nx config as TypeScript', async () => {
    await open('/coverage/setup');

    const workflows = snippets().filter(s => s.language() === 'yaml');
    expect(workflows).toHaveLength(7);
    for (const workflow of workflows) expect(workflow.code()).toContain(`url: ${location.origin}`);
    expect(snippets().filter(s => s.language() === 'ts')).toHaveLength(1);
  });
});
