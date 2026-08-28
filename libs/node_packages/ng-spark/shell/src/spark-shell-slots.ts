import { Directive, inject, input, TemplateRef } from '@angular/core';

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

/**
 * Topbar, trailing edge, **beside** the default chrome rather than instead of it — rendered before
 * the language selector, which stays.
 *
 * ## Why this is not just `*sparkShellTopbarEnd`
 *
 * That slot REPLACES the region, which is right for an auth bar (a host taking over the trailing
 * edge wholesale) and wrong for a page-level action button. A host that only wanted to add a
 * button had to re-render the language selector itself to keep it — which means importing it,
 * knowing it hides itself in a single-language app, and keeping that copy in step with the shell.
 * Every host that did this got it slightly differently.
 *
 * Pairs with `SparkQueryActionsService`: that service resolves a query's custom actions without the
 * grid, and this is where a page-level one goes.
 *
 * ```html
 * <button *sparkShellTopbarActions class="btn btn-primary" (click)="publish()">Publish</button>
 * ```
 *
 * Supplying both slots is allowed and does what it says: `topbarEnd` replaces the default chrome,
 * and these actions still render ahead of whatever it put there.
 */
@Directive({ selector: '[sparkShellTopbarActions]' })
export class SparkShellTopbarActionsDirective {
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
 * An extra accordion tab in the sidebar menu, rendered after the generated program-unit groups
 * and sharing their single-open behavior.
 *
 * A tab is contributed as DATA (header + body template), not as markup, and that is load-bearing:
 * `bs-accordion` discovers its tabs with an Angular content query, which matches by declaration
 * view, so a `<bs-accordion-tab>` written in a host's template and inserted into the library's
 * accordion is never registered — it would land at index -1, get no hoisted header and no slot.
 * Declaring a second `<bs-accordion>` instead is what puts the tab in its own exclusivity group:
 * `mp-accordion` enforces single-open per element, over children it owns and over
 * `<details name>`, whose grouping cannot cross a shadow root. So the tab element must be created
 * by the menu itself, from what this directive carries.
 *
 * ```html
 * <ng-container *sparkShellTab="'Component demos'; icon: 'palette'">
 *   <a routerLink="/query-slots" routerLinkActive="active" class="nav-link">Query card slots</a>
 * </ng-container>
 * ```
 *
 * Navigation still belongs in `programUnits.json` — this is for pages the model cannot describe
 * (client-side demos, external tools). For sidebar content that is NOT an accordion tab, use
 * `*sparkShellSidebarTop` or `*sparkShellSidebarFooter`.
 */
@Directive({ selector: '[sparkShellTab]' })
export class SparkShellTabDirective {
  readonly templateRef = inject<TemplateRef<unknown>>(TemplateRef);

  /** The tab's header label. */
  readonly header = input.required<string>({ alias: 'sparkShellTab' });

  /** Bootstrap icon name for the header, as in `programUnits.json`. Defaults to a folder. */
  readonly icon = input<string | undefined>(undefined, { alias: 'sparkShellTabIcon' });

  /** Replaces the icon+label header entirely, for a header that needs its own markup. */
  readonly headerTemplate = input<TemplateRef<unknown> | null>(null, { alias: 'sparkShellTabHeader' });
}

/**
 * A sidebar accordion tab in the shape the menu renders it. Hosts normally contribute tabs with
 * `*sparkShellTab`; this is the same thing as data, for a host that computes its tabs.
 */
export interface SparkSidebarTab {
  readonly header: string;
  readonly icon?: string;
  readonly headerTemplate?: TemplateRef<unknown> | null;
  readonly content: TemplateRef<unknown>;
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
