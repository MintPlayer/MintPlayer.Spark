import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { BsThemeService, BsThemeToggleComponent } from '@mintplayer/ng-bootstrap/theming';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { SparkShellComponent } from './spark-shell.component';
import {
  SparkShellMainHeaderDirective,
  SparkShellSidebarHeaderDirective,
  SparkShellSidebarTopDirective,
  SparkShellTabDirective,
  SparkShellTopbarEndDirective,
  SparkShellTopbarActionsDirective,
  SparkShellTopbarStartDirective,
} from './spark-shell-slots';
import { SparkService } from '@mintplayer/ng-spark/services';
import { settle } from '../../src/test-utils';

/**
 * The shell's slot contract, same doctrine as spark-query-card: an omitted slot renders its
 * default (toggler, language selector, title heading), a supplied one replaces exactly that
 * region and nothing else. The menu is not a slot and always renders.
 */

describe('SparkShellComponent', () => {
  const getProgramUnits = vi.fn(async () => ({ programUnitGroups: [] }));
  const fixtures: ComponentFixture<unknown>[] = [];

  beforeEach(() => {
    getProgramUnits.mockClear();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: SparkService, useValue: { getProgramUnits } },
      ],
    });
  });

  // Destroy while the DOM is still intact, and give anything the teardown itself schedules a
  // turn to finish — the other half of the note on settle().
  afterEach(async () => {
    for (const fixture of fixtures.splice(0)) {
      fixture.destroy();
    }
    await new Promise(resolve => setTimeout(resolve, 0));
  });

  async function render(template: string) {
    @Component({
      standalone: true,
      imports: [
        SparkShellComponent,
        SparkShellTopbarStartDirective, SparkShellTopbarEndDirective, SparkShellTopbarActionsDirective,
        SparkShellSidebarHeaderDirective, SparkShellSidebarTopDirective,
        SparkShellMainHeaderDirective, SparkShellTabDirective,
      ],
      template,
    })
    class Host {}

    const fixture = TestBed.createComponent(Host);
    fixtures.push(fixture);
    fixture.detectChanges();
    await settle(fixture, { macrotask: true });
    return fixture;
  }

  it('renders every default when no slot is supplied', async () => {
    const fixture = await render(`<spark-shell title="My App"><div class="routed">content</div></spark-shell>`);
    const el: HTMLElement = fixture.nativeElement;

    expect(el.querySelector('bs-navbar-toggler')).toBeTruthy();          // TopbarStart default
    expect(el.querySelector('spark-language-selector')).toBeTruthy();    // TopbarEnd default
    expect(el.querySelector('nav h5')?.textContent?.trim()).toBe('My App'); // SidebarHeader default
    expect(el.querySelector('spark-program-units')).toBeTruthy();        // the menu, never a slot
    expect(el.querySelector('main .routed')?.textContent).toBe('content'); // default ng-content = main
  });

  it('a supplied slot replaces its region and leaves the others alone', async () => {
    const fixture = await render(`
      <spark-shell title="Ignored">
        <span *sparkShellTopbarEnd class="my-auth">auth</span>
        <h4 *sparkShellSidebarHeader class="my-brand">Brand</h4>
        <div class="routed"></div>
      </spark-shell>`);
    const el: HTMLElement = fixture.nativeElement;

    expect(el.querySelector('.my-auth')).toBeTruthy();
    expect(el.querySelector('spark-language-selector')).toBeNull();      // replaced
    expect(el.querySelector('.my-brand')).toBeTruthy();
    expect(el.querySelector('nav h5')).toBeNull();                       // replaced
    expect(el.querySelector('bs-navbar-toggler')).toBeTruthy();          // untouched default
    expect(el.querySelector('spark-program-units')).toBeTruthy();        // not replaceable
  });

  // ----------------------------------------------------------------------------------
  // #327 §9.7 — *sparkShellTopbarActions sits BESIDE the trailing chrome, not instead of it
  // ----------------------------------------------------------------------------------

  it('topbar actions render alongside the language selector, not in place of it', async () => {
    // The whole reason this slot exists. *sparkShellTopbarEnd REPLACES the region, which is right
    // for an auth bar and wrong for a page-level button: a host that only wanted to add a button
    // had to re-render the language selector itself to keep it.
    const fixture = await render(`
      <spark-shell title="My App">
        <button *sparkShellTopbarActions class="my-action">Publish</button>
        <div class="routed"></div>
      </spark-shell>`);
    const el: HTMLElement = fixture.nativeElement;

    expect(el.querySelector('.my-action')).toBeTruthy();
    expect(el.querySelector('spark-language-selector')).toBeTruthy();   // still there
  });

  it('topbar actions render ahead of the trailing chrome', async () => {
    const fixture = await render(`
      <spark-shell title="My App">
        <button *sparkShellTopbarActions class="my-action">Publish</button>
        <div class="routed"></div>
      </spark-shell>`);
    const el: HTMLElement = fixture.nativeElement;

    const action = el.querySelector('.my-action')!;
    const selector = el.querySelector('spark-language-selector')!;
    // DOCUMENT_POSITION_FOLLOWING === the selector comes after the action.
    expect(action.compareDocumentPosition(selector) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('topbar actions survive a host that also replaces the trailing edge', async () => {
    // Supplying both is allowed and additive: topbarEnd replaces the default chrome, and the
    // actions still render ahead of whatever it put there.
    const fixture = await render(`
      <spark-shell title="My App">
        <button *sparkShellTopbarActions class="my-action">Publish</button>
        <span *sparkShellTopbarEnd class="my-auth">auth</span>
        <div class="routed"></div>
      </spark-shell>`);
    const el: HTMLElement = fixture.nativeElement;

    expect(el.querySelector('.my-action')).toBeTruthy();
    expect(el.querySelector('.my-auth')).toBeTruthy();
    expect(el.querySelector('spark-language-selector')).toBeNull();     // topbarEnd replaced it
  });

  it('renders nothing extra in the topbar when the actions slot is omitted', async () => {
    const fixture = await render(`<spark-shell title="My App"><div class="routed"></div></spark-shell>`);

    expect(fixture.nativeElement.querySelector('.my-action')).toBeNull();
    expect(fixture.nativeElement.querySelector('spark-language-selector')).toBeTruthy();
  });

  it('renders the empty-by-default regions only when supplied', async () => {
    const without = await render(`<spark-shell><div class="routed"></div></spark-shell>`);
    expect(without.nativeElement.querySelector('.extra-link')).toBeNull();
    expect(without.nativeElement.querySelector('.main-alert')).toBeNull();

    const withSlots = await render(`
      <spark-shell>
        <a *sparkShellSidebarTop class="extra-link">Extra</a>
        <div *sparkShellMainHeader class="main-alert">alert</div>
        <div class="routed"></div>
      </spark-shell>`);
    const el: HTMLElement = withSlots.nativeElement;
    expect(el.querySelector('nav .extra-link')).toBeTruthy();
    expect(el.querySelector('main .main-alert')).toBeTruthy();
  });

  // The regression this guards: a host that declared its own <bs-accordion> got a SECOND
  // accordion in the sidebar, and single-open is enforced per accordion element (over its own
  // children and over `<details name>`, which cannot group across a shadow root) — so a host tab
  // and a generated group could be open at the same time. The tab must be created by the menu.
  it('a *sparkShellTab tab joins the menu accordion rather than starting a second one', async () => {
    const fixture = await render(`
      <spark-shell>
        <ng-container *sparkShellTab="'Component demos'; icon: 'palette'">
          <a class="demo-link">Query card slots</a>
        </ng-container>
        <div class="routed"></div>
      </spark-shell>`);
    const el: HTMLElement = fixture.nativeElement;

    const accordions = el.querySelectorAll('nav bs-accordion');
    expect(accordions.length).toBe(1);

    const link = el.querySelector('.demo-link');
    expect(link).toBeTruthy();
    expect(accordions[0].contains(link!)).toBe(true);
    expect(el.textContent).toContain('Component demos');
  });

  it('sidebarTheme drives data-bs-theme on the sidebar nav', async () => {
    const dark = await render(`<spark-shell><div class="routed"></div></spark-shell>`);
    expect(dark.nativeElement.querySelector('nav')?.getAttribute('data-bs-theme')).toBe('dark');

    const light = await render(`<spark-shell sidebarTheme="light"><div class="routed"></div></spark-shell>`);
    expect(light.nativeElement.querySelector('nav')?.getAttribute('data-bs-theme')).toBe('light');
  });

  describe('theme toggle', () => {
    let themeServicesCreated = 0;

    beforeEach(() => {
      themeServicesCreated = 0;
      TestBed.overrideProvider(BsThemeService, {
        useFactory: () => {
          themeServicesCreated++;
          return { mode: signal('auto'), effectiveMode: signal('light'), setMode: vi.fn() };
        },
      });
    });

    it('renders bs-theme-toggle in the topbar by default, with Spark-translated modes', async () => {
      const fixture = await render(`<spark-shell><div class="routed"></div></spark-shell>`);
      const toggle = fixture.debugElement.query(By.directive(BsThemeToggleComponent));

      expect(toggle).toBeTruthy();
      expect((toggle.nativeElement as HTMLElement).closest('.spark-topbar')).toBeTruthy();
      // No translations are loaded in the test, so SparkLanguageService.t() echoes the key: what
      // is pinned here is that every label and announcement goes through the theme.* keys.
      const modes = (toggle.componentInstance as BsThemeToggleComponent).modes();
      expect(modes.map(m => [m.mode, m.label, m.announcement])).toEqual([
        ['auto', 'theme.switchToAuto', 'theme.auto'],
        ['light', 'theme.switchToLight', 'theme.light'],
        ['dark', 'theme.switchToDark', 'theme.dark'],
      ]);
      expect(themeServicesCreated).toBe(1);
    });

    it('[themeToggle]="false" hides the toggle but still instantiates BsThemeService', async () => {
      const fixture = await render(`<spark-shell [themeToggle]="false"><div class="routed"></div></spark-shell>`);

      expect(fixture.debugElement.query(By.directive(BsThemeToggleComponent))).toBeNull();
      expect(fixture.nativeElement.querySelector('bs-theme-toggle')).toBeNull();
      expect(themeServicesCreated).toBe(1);
    });

    it('keeps the toggle when the host replaces the trailing topbar edge', async () => {
      const fixture = await render(`
        <spark-shell>
          <span *sparkShellTopbarEnd class="custom-end">me</span>
          <div class="routed"></div>
        </spark-shell>`);

      expect(fixture.nativeElement.querySelector('.custom-end')).toBeTruthy();
      expect(fixture.nativeElement.querySelector('bs-theme-toggle')).toBeTruthy();
    });
  });
});
