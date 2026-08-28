import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { SparkShellComponent } from './spark-shell.component';
import {
  SparkShellMainHeaderDirective,
  SparkShellSidebarHeaderDirective,
  SparkShellSidebarTopDirective,
  SparkShellTopbarEndDirective,
  SparkShellTopbarStartDirective,
} from './spark-shell-slots';
import { SparkService } from '@mintplayer/ng-spark/services';

/**
 * The shell's slot contract, same doctrine as spark-query-card: an omitted slot renders its
 * default (toggler, language selector, title heading), a supplied one replaces exactly that
 * region and nothing else. The menu is not a slot and always renders.
 */

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  for (let i = 0; i < 5; i++) {
    await fixture.whenStable();
    await Promise.resolve();
    fixture.detectChanges();
  }
  await fixture.whenStable();
  fixture.detectChanges();
}

describe('SparkShellComponent', () => {
  const getProgramUnits = vi.fn(async () => ({ programUnitGroups: [] }));

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

  async function render(template: string) {
    @Component({
      standalone: true,
      imports: [
        SparkShellComponent,
        SparkShellTopbarStartDirective, SparkShellTopbarEndDirective,
        SparkShellSidebarHeaderDirective, SparkShellSidebarTopDirective,
        SparkShellMainHeaderDirective,
      ],
      template,
    })
    class Host {}

    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    await settle(fixture);
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

  it('sidebarTheme drives data-bs-theme on the sidebar nav', async () => {
    const dark = await render(`<spark-shell><div class="routed"></div></spark-shell>`);
    expect(dark.nativeElement.querySelector('nav')?.getAttribute('data-bs-theme')).toBe('dark');

    const light = await render(`<spark-shell sidebarTheme="light"><div class="routed"></div></spark-shell>`);
    expect(light.nativeElement.querySelector('nav')?.getAttribute('data-bs-theme')).toBe('light');
  });
});
