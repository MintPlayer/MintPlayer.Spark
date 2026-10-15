import { Component, computed, signal, TemplateRef, viewChild } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { SparkProgramUnitsComponent } from './spark-program-units.component';
import { SPARK_AUTH_STATE } from '@mintplayer/ng-spark';
import { SparkLanguageService, SparkService } from '@mintplayer/ng-spark/services';
import { ProgramUnitsConfiguration } from '@mintplayer/ng-spark/models';
import { settle, StubComponent } from '../../src/test-utils';

/**
 * The menu's contract: everything rendered comes from the server response (consumers write no
 * router links), both levels are sorted by `order`, and the caller-scoped response is re-fetched
 * whenever the auth signal or the reload token changes.
 */

const config: ProgramUnitsConfiguration = {
  programUnitGroups: [
    {
      id: 'g2', name: { en: 'Second' }, order: 2,
      programUnits: [
        { id: 'u-url', name: { en: 'Status' }, type: 'url', url: 'https://status.example.com', order: 1 },
      ],
    },
    {
      id: 'g1', name: { en: 'First' }, order: 1,
      programUnits: [
        { id: 'u-b', name: { en: 'B' }, type: 'query', queryId: 'q-b', alias: 'bees', order: 2 },
        { id: 'u-a', name: { en: 'A' }, type: 'persistentObject', persistentObjectId: 'po-a', alias: 'start-page', objectId: 'start', order: 1 },
      ],
    },
  ],
};

/**
 * The url units, classified against the page's origin and the router's base href (`sparkAppUrl`): into the
 * application, out of it, and neither.
 */
const urlConfig = (): ProgramUnitsConfiguration => ({
  programUnitGroups: [
    {
      id: 'g', name: { en: 'Links' }, order: 1,
      programUnits: [
        { id: 'u-setup', name: { en: 'Setup' }, type: 'url', url: '/merge-queue/setup?provider=github#permissions', order: 1 },
        { id: 'u-history', name: { en: 'History' }, type: 'url', url: `${location.origin}/merge-queue/history/`, order: 2 },
        { id: 'u-status', name: { en: 'Status' }, type: 'url', url: 'https://status.example.com', order: 3 },
        { id: 'u-script', name: { en: 'Script' }, type: 'url', url: 'javascript:alert(1)', order: 4 },
        { id: 'u-tab', name: { en: 'Tab' }, type: 'url', url: 'java\tscript:alert(1)', order: 5 },
        { id: 'u-data', name: { en: 'Data' }, type: 'url', url: 'data:text/html,<b>x</b>', order: 6 },
        { id: 'u-cars', name: { en: 'Cars' }, type: 'query', queryId: 'q-cars', alias: 'cars', order: 7 },
      ],
    },
  ],
});

/** The anchor whose label is `label`. */
function anchorLabelled(el: HTMLElement, label: string): HTMLAnchorElement {
  const anchor = Array.from(el.querySelectorAll('a')).find(a => a.querySelector('.ms-2')?.textContent?.trim() === label);
  expect(anchor, `an anchor labelled ${label}`).toBeTruthy();
  return anchor!;
}

describe('SparkProgramUnitsComponent', () => {
  const getProgramUnits = vi.fn(async () => config);

  beforeEach(() => {
    getProgramUnits.mockClear();
    TestBed.configureTestingModule({
      imports: [SparkProgramUnitsComponent],
      providers: [
        provideRouter([{ path: '**', component: StubComponent }]),
        { provide: SparkService, useValue: { getProgramUnits } },
        { provide: SparkLanguageService, useValue: { t: (key: string) => key === 'common.opensInNewTab' ? '(opens in a new tab)' : key } },
      ],
    });
  });

  async function render(): Promise<HTMLElement> {
    const fixture = TestBed.createComponent(SparkProgramUnitsComponent);
    fixture.detectChanges();
    await settle(fixture, { macrotask: true });
    return fixture.nativeElement;
  }

  it('renders groups and units sorted by order, links sourced from the response', async () => {
    const el = await render();

    const anchors = Array.from(el.querySelectorAll('a'));
    // g1 (order 1) before g2 (order 2); within g1, u-a (order 1) before u-b (order 2).
    expect(anchors.map(a => a.querySelector('.ms-2')?.textContent?.trim())).toEqual(['A', 'B', 'Status']);
    // Deep link: alias + objectId.
    expect(anchors[0].getAttribute('href')).toBe('/po/start-page/start');
    expect(anchors[1].getAttribute('href')).toBe('/query/bees');
  });

  it('renders a url unit to another site as a new-tab anchor to the parsed href, announced to a screen reader', async () => {
    getProgramUnits.mockResolvedValueOnce(urlConfig());
    const el = await render();

    const external = anchorLabelled(el, 'Status');
    // The parsed href, never the raw string from the file.
    expect(external.getAttribute('href')).toBe('https://status.example.com/');
    expect(external.getAttribute('target')).toBe('_blank');
    expect(external.getAttribute('rel')).toBe('noopener noreferrer');
    expect(external.querySelector('.visually-hidden')?.textContent?.trim()).toBe('(opens in a new tab)');
    // Without an icon of its own, its fallback icon already is the external mark: the glyph is not shown twice.
    expect(external.querySelectorAll('spark-icon').length).toBe(1);
    expect(external.querySelector('.spark-external-mark')).toBeNull();
  });

  it('marks a url unit to another site that has its own icon with the external mark after its name, hidden from a screen reader', async () => {
    getProgramUnits.mockResolvedValueOnce({
      programUnitGroups: [{
        id: 'g', name: { en: 'Links' }, order: 1,
        programUnits: [{ id: 'u-docs', name: { en: 'Docs' }, icon: 'bi-book', type: 'url', url: 'https://docs.example.com', order: 1 }],
      }],
    });
    const el = await render();

    const external = anchorLabelled(el, 'Docs');
    expect(external.querySelectorAll('spark-icon').length).toBe(2);
    expect(external.querySelector('.spark-external-mark')?.getAttribute('aria-hidden')).toBe('true');
  });

  it('renders a url unit into the application as a router link in the same tab, query and fragment kept', async () => {
    getProgramUnits.mockResolvedValueOnce(urlConfig());
    const el = await render();

    const setup = anchorLabelled(el, 'Setup');
    expect(setup.getAttribute('href')).toBe('/merge-queue/setup?provider=github#permissions');
    expect(setup.hasAttribute('target')).toBe(false);
    expect(setup.hasAttribute('rel')).toBe(false);
    expect(setup.querySelector('.visually-hidden')).toBeNull();
    // Absolute on this origin, at the base: the route, without its trailing slash.
    expect(anchorLabelled(el, 'History').getAttribute('href')).toBe('/merge-queue/history');
  });

  it('renders nothing for a url that is not http(s)', async () => {
    getProgramUnits.mockResolvedValueOnce(urlConfig());
    const el = await render();

    const labels = Array.from(el.querySelectorAll('a')).map(a => a.querySelector('.ms-2')?.textContent?.trim());
    expect(labels).toEqual(['Setup', 'History', 'Status', 'Cars']);
    expect(Array.from(el.querySelectorAll('a')).map(a => a.getAttribute('href') ?? '').filter(href => !/^(\/|https:)/.test(href))).toEqual([]);
  });

  it('marks the current page with aria-current; an in-app url is current on exactly its path', async () => {
    getProgramUnits.mockResolvedValueOnce(urlConfig());
    const fixture = TestBed.createComponent(SparkProgramUnitsComponent);
    fixture.detectChanges();
    await settle(fixture, { macrotask: true });
    const router = TestBed.inject(Router);
    const el: HTMLElement = fixture.nativeElement;

    // The page's query holds the link's own (provider=github); the fragment does not matter.
    await router.navigateByUrl('/merge-queue/setup?provider=github&account=acme');
    await settle(fixture, { macrotask: true });
    expect(anchorLabelled(el, 'Setup').getAttribute('aria-current')).toBe('page');
    expect(anchorLabelled(el, 'Setup').classList).toContain('active');
    expect(anchorLabelled(el, 'Cars').hasAttribute('aria-current')).toBe(false);

    // Below the url's path is not the url's page.
    await router.navigateByUrl('/merge-queue/setup/child?provider=github');
    await settle(fixture, { macrotask: true });
    expect(anchorLabelled(el, 'Setup').hasAttribute('aria-current')).toBe(false);

    // A query unit stays current on its page and below it, as before.
    await router.navigateByUrl('/query/cars/details');
    await settle(fixture, { macrotask: true });
    expect(anchorLabelled(el, 'Cars').getAttribute('aria-current')).toBe('page');
  });

  it('renders extraTabs after the generated groups, inside the one accordion', async () => {
    @Component({
      standalone: true,
      imports: [SparkProgramUnitsComponent],
      template: `
        <ng-template #extra><a class="extra-link">Query card slots</a></ng-template>
        <spark-program-units [extraTabs]="tabs()" />`,
    })
    class Host {
      readonly extra = viewChild<TemplateRef<unknown>>('extra');
      readonly tabs = computed(() => {
        const content = this.extra();
        return content ? [{ header: 'Component demos', content }] : [];
      });
    }

    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    await settle(fixture, { macrotask: true });

    const el: HTMLElement = fixture.nativeElement;
    const accordions = el.querySelectorAll('bs-accordion');
    expect(accordions.length).toBe(1);

    const tabs = accordions[0].querySelectorAll('bs-accordion-tab');
    expect(tabs.length).toBe(3);                              // two server groups, then the extra
    expect(tabs[2].querySelector('.extra-link')).toBeTruthy();
    expect(el.textContent).toContain('Component demos');
  });

  it('re-fetches when the SPARK_AUTH_STATE signal changes (sign-in/out)', async () => {
    const authState = signal<unknown>(null);
    TestBed.overrideProvider(SPARK_AUTH_STATE, { useValue: authState.asReadonly() });

    const fixture = TestBed.createComponent(SparkProgramUnitsComponent);
    fixture.detectChanges();
    await settle(fixture, { macrotask: true });
    expect(getProgramUnits).toHaveBeenCalledTimes(1);

    authState.set({ userName: 'alice' });
    await settle(fixture, { macrotask: true });
    expect(getProgramUnits).toHaveBeenCalledTimes(2);
  });

  it('fetches exactly once without an auth provider, and again on reloadToken change', async () => {
    @Component({
      standalone: true,
      imports: [SparkProgramUnitsComponent],
      template: `<spark-program-units [reloadToken]="token()" />`,
    })
    class Host {
      readonly token = signal(0);
    }

    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    await settle(fixture, { macrotask: true });
    expect(getProgramUnits).toHaveBeenCalledTimes(1);

    fixture.componentInstance.token.set(1);
    await settle(fixture, { macrotask: true });
    expect(getProgramUnits).toHaveBeenCalledTimes(2);
  });
});
