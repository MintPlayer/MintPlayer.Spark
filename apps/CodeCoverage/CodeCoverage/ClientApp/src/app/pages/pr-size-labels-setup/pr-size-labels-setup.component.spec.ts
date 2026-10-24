import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { settle } from '../../../testing/test-utils';
import PrSizeLabelsSetupComponent from './pr-size-labels-setup.component';

const STEPS = ['enable', 'scale', 'ignore', 'check-run', 'disable', 'adopt', 'forges'];

describe('PrSizeLabelsSetupComponent', () => {
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
        provideRouter([{ path: 'pr-size-labels/setup', component: PrSizeLabelsSetupComponent }]),
      ],
    });
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url, PrSizeLabelsSetupComponent);
    await settle(harness.fixture);
  }

  const el = () => harness.fixture.nativeElement as HTMLElement;
  const all = <T extends Element>(selector: string) => [...el().querySelectorAll<T>(selector)];
  const text = (testId: string) => el().querySelector(`[data-testid="${testId}"]`)?.textContent ?? '';
  const stepLinks = () => all<HTMLAnchorElement>('[data-testid="ps-setup-steps"] a');
  const rows = (testId: string) => all<HTMLTableRowElement>(`[data-testid="${testId}"] tbody tr`)
    .map(row => [...row.cells].map(cell => cell.textContent!.trim()));

  it('walks through the steps in order, each numbered and listed with a link to its anchor', async () => {
    await open('/pr-size-labels/setup');

    const cards = all<HTMLElement>('[data-step]');
    expect(cards.map(card => card.id)).toEqual(STEPS);
    cards.forEach((card, i) => expect(card.querySelector('h2 .badge')?.textContent?.trim()).toBe(String(i + 1)));
    expect(stepLinks().map(a => a.getAttribute('href'))).toEqual(STEPS.map(id => `/pr-size-labels/setup#${id}`));
  });

  it('scrolls to the step it is opened at, and to a step chosen from the list', async () => {
    await open('/pr-size-labels/setup#ignore');
    expect(scrolledTo).toEqual(['ignore']);

    stepLinks()[STEPS.indexOf('disable')].click();
    await settle(harness.fixture);
    expect(scrolledTo).toEqual(['ignore', 'disable']);
  });

  // PRD D1: an account default, and three choices per repository.
  it('explains the account switch and every repository setting', async () => {
    await open('/pr-size-labels/setup');

    expect(text('ps-setup-account')).toContain('PR size labels');
    expect(rows('ps-setup-policies').map(([value]) => value)).toEqual(['Use account setting', 'Label pull requests', 'Never label']);
  });

  // PRD D2/D5: the tables are the defaults a new account starts with (PrSizeDefaults), in order.
  it('lists the default scale and the default ignore rules in order', async () => {
    await open('/pr-size-labels/setup');

    expect(rows('ps-setup-scale').map(([name, from]) => `${name} ${from}`))
      .toEqual(['size/XS 0', 'size/S 10', 'size/M 30', 'size/L 100', 'size/XL 500', 'size/XXL 1000']);
    expect(rows('ps-setup-ignore').map(([glob]) => glob)).toEqual([
      '**/*.json', '**/*Tests/**', '**/package-lock.json', '**/packages.lock.json', '**/Directory.Packages.props', '**/*.md',
    ]);
    expect(text('ps-setup-last-wins')).toContain('The last matching rule wins');
  });

  // PRD D3, D4.3, D7, D8: what a rename and a delete do, the prompt, the check, and Bitbucket.
  it('says what a rename, a delete and a disable do, that the check is never red, and that Bitbucket has no labels', async () => {
    await open('/pr-size-labels/setup');

    expect(text('ps-setup-scale-changes')).toContain('Renaming a row renames the label');
    expect(text('ps-setup-scale-changes')).toContain('Deleting a row deletes its label');
    expect(text('ps-setup-disable')).toContain('Remove labels');
    expect(text('ps-setup-disable')).toContain('Keep labels');
    expect(text('ps-setup-neutral')).toContain('neutral');
    expect(text('ps-setup-adopt')).toContain('adopted');
    expect(text('ps-setup-forges')).toContain('Bitbucket pull requests have no labels');
  });
});
