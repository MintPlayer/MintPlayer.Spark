import { Directive, inject, TemplateRef } from '@angular/core';

/**
 * Region slots for `<spark-shell>`.
 *
 * Each directive marks a template that REPLACES one region of the shell chrome (or fills an
 * empty one). An omitted slot is not an empty slot: the shell renders its default — the toggler,
 * the language selector, the title heading — so a host that supplies nothing still gets a
 * complete working shell, and a host that supplies one slot leaves the rest alone.
 *
 * The menu itself is deliberately NOT a slot. Navigation is sourced entirely from
 * `programUnits.json` through the rights-filtered `/spark/program-units` endpoint and re-fetched
 * on sign-in/out; a host that finds itself writing unit anchors in a slot should be adding units
 * to `programUnits.json` instead. Slots exist for the content AROUND the menu: an auth bar, a
 * user chip, branding, a one-off extra link, an alert strip above the routed content.
 *
 * Naming follows the house convention (prefix, component, slot — see `*sparkQueryIcon` in
 * `@mintplayer/ng-spark/grid`): `sparkShell` + region. Every slot also exists as a `TemplateRef`
 * input on `SparkShellComponent` for hosts that cannot use content projection.
 *
 * ```html
 * <spark-shell title="My App">
 *   <spark-auth-bar *sparkShellTopbarEnd />
 *   <div *sparkShellSidebarTop>
 *     <a routerLink="/github-projects" class="nav-link">GitHub projects</a>
 *   </div>
 *   <router-outlet />
 * </spark-shell>
 * ```
 */

/** Topbar, leading edge. Default: a `bs-navbar-toggler` mirroring the shell's open state. */
@Directive({ selector: '[sparkShellTopbarStart]' })
export class SparkShellTopbarStartDirective {
  readonly templateRef = inject<TemplateRef<unknown>>(TemplateRef);
}

/**
 * Topbar, trailing edge. Default: the language selector (which hides itself when the app has
 * one language). This is where an auth bar goes — the shell cannot ship one itself, since
 * `@mintplayer/ng-spark` does not (and must not) depend on `@mintplayer/ng-spark-auth`.
 */
@Directive({ selector: '[sparkShellTopbarEnd]' })
export class SparkShellTopbarEndDirective {
  readonly templateRef = inject<TemplateRef<unknown>>(TemplateRef);
}

/** Sidebar, above everything. Default: `<h5>{{ title }}</h5>`. */
@Directive({ selector: '[sparkShellSidebarHeader]' })
export class SparkShellSidebarHeaderDirective {
  readonly templateRef = inject<TemplateRef<unknown>>(TemplateRef);
}

/** Sidebar, between the header and the program-units menu. No default. */
@Directive({ selector: '[sparkShellSidebarTop]' })
export class SparkShellSidebarTopDirective {
  readonly templateRef = inject<TemplateRef<unknown>>(TemplateRef);
}

/**
 * Sidebar, directly below the program-units menu. No default. For extra accordion tabs,
 * declare a complete `<bs-accordion>` with your tabs inside the template — the accordion and
 * its tabs must be declared together for the tab discovery to work across the template boundary.
 */
@Directive({ selector: '[sparkShellSidebarTabs]' })
export class SparkShellSidebarTabsDirective {
  readonly templateRef = inject<TemplateRef<unknown>>(TemplateRef);
}

/** Sidebar, at the very bottom. No default. */
@Directive({ selector: '[sparkShellSidebarFooter]' })
export class SparkShellSidebarFooterDirective {
  readonly templateRef = inject<TemplateRef<unknown>>(TemplateRef);
}

/** Main region, above the projected content (the host's `<router-outlet>`). No default. */
@Directive({ selector: '[sparkShellMainHeader]' })
export class SparkShellMainHeaderDirective {
  readonly templateRef = inject<TemplateRef<unknown>>(TemplateRef);
}
