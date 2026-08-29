import { ChangeDetectionStrategy, Component, computed, contentChild, contentChildren, input, signal, TemplateRef } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { BsShellComponent, BsShellSidebarDirective, BsShellState } from '@mintplayer/ng-bootstrap/shell';
import { BsNavbarTogglerComponent } from '@mintplayer/ng-bootstrap/navbar-toggler';
import type { Breakpoint } from '@mintplayer/ng-bootstrap';
import type { ShellStateChangeEventDetail } from '@mintplayer/web-components/shell';
import { SparkProgramUnitsComponent } from './spark-program-units.component';
import { SparkLanguageSelectorComponent } from './spark-language-selector.component';
import {
  SparkShellMainHeaderDirective,
  SparkShellSidebarFooterDirective,
  SparkShellSidebarHeaderDirective,
  SparkShellSidebarTopDirective,
  SparkShellTabDirective,
  SparkShellTopbarEndDirective,
  SparkShellTopbarActionsDirective,
  SparkShellTopbarStartDirective,
  SparkSidebarTab,
} from './spark-shell-slots';

/**
 * The application frame: topbar + sidebar + main, wrapping ng-bootstrap's `bs-shell` (whose
 * `mp-shell` web component owns ALL responsive behavior — breakpoints, the overlay drawer,
 * dismiss-on-navigate — in CSS; nothing here re-derives a pixel width). The sidebar renders the
 * server-driven program-units menu; the host projects its `<router-outlet>` as the default
 * content and customizes the chrome through the `*sparkShell*` slots (see `spark-shell-slots.ts`
 * for the doctrine: an omitted slot renders its default, and the menu itself is never a slot).
 *
 * ```html
 * <spark-shell title="My App">
 *   <spark-auth-bar *sparkShellTopbarEnd />
 *   <router-outlet />
 * </spark-shell>
 * ```
 *
 * The one piece of state the shell keeps is the toggler↔drawer mirror: the built-in hamburger is
 * hidden (`::part(hamburger)`) in favor of a `bs-navbar-toggler` in the topbar, so the shell
 * listens to `statechange` to keep the toggler's icon truthful in `auto` mode and only forces
 * `show`/`hide` on explicit toggles.
 *
 * Theming: the chrome colors are CSS custom properties with the classic dark-sidebar defaults —
 * `--spark-shell-topbar-bg`, `--spark-shell-sidebar-bg`, `--spark-shell-main-bg` — overridable on
 * the `<spark-shell>` element. `sidebarTheme` flips the sidebar's `data-bs-theme` (which is what
 * recolors the accordion internals across the shadow boundary) together with its default palette.
 */
@Component({
  selector: 'spark-shell',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    NgTemplateOutlet,
    BsShellComponent, BsShellSidebarDirective, BsNavbarTogglerComponent,
    SparkProgramUnitsComponent, SparkLanguageSelectorComponent,
  ],
  templateUrl: './spark-shell.component.html',
  styleUrl: './spark-shell.component.scss',
})
export class SparkShellComponent {
  /** The sidebar heading. Ignored when a `*sparkShellSidebarHeader` slot is supplied. */
  readonly title = input('');

  /** Forwarded to `bs-shell`: below it the sidebar is an overlay drawer. */
  readonly breakpoint = input<Breakpoint>('md');

  /**
   * `data-bs-theme` for the sidebar — what flips the accordion's shadow-DOM internals between
   * palettes — plus the matching default background. `null` sets no theme (inherit the page's).
   */
  readonly sidebarTheme = input<'dark' | 'light' | null>('dark');

  /** Forwarded to the menu: any changed value re-fetches the program units. */
  readonly reloadToken = input<unknown>(null);

  /** Extra sidebar tabs as data, for hosts that compute them; `*sparkShellTab` is the usual way. */
  readonly sidebarTabs = input<readonly SparkSidebarTab[]>([]);

  // One TemplateRef input per slot, for hosts that cannot use content projection
  // (the spark-query-card precedent). The projected directive wins when both are present.
  readonly topbarStartTemplate = input<TemplateRef<unknown> | null>(null);
  readonly topbarEndTemplate = input<TemplateRef<unknown> | null>(null);
  readonly topbarActionsTemplate = input<TemplateRef<unknown> | null>(null);
  readonly sidebarHeaderTemplate = input<TemplateRef<unknown> | null>(null);
  readonly sidebarTopTemplate = input<TemplateRef<unknown> | null>(null);
  readonly sidebarFooterTemplate = input<TemplateRef<unknown> | null>(null);
  readonly mainHeaderTemplate = input<TemplateRef<unknown> | null>(null);

  private readonly topbarStartSlot = contentChild(SparkShellTopbarStartDirective);
  private readonly topbarEndSlot = contentChild(SparkShellTopbarEndDirective);
  private readonly topbarActionsSlot = contentChild(SparkShellTopbarActionsDirective);
  private readonly sidebarHeaderSlot = contentChild(SparkShellSidebarHeaderDirective);
  private readonly sidebarTopSlot = contentChild(SparkShellSidebarTopDirective);
  private readonly sidebarFooterSlot = contentChild(SparkShellSidebarFooterDirective);
  private readonly mainHeaderSlot = contentChild(SparkShellMainHeaderDirective);

  protected readonly topbarStartTpl = computed(() => this.topbarStartSlot()?.templateRef ?? this.topbarStartTemplate());
  protected readonly topbarEndTpl = computed(() => this.topbarEndSlot()?.templateRef ?? this.topbarEndTemplate());
  protected readonly topbarActionsTpl = computed(() => this.topbarActionsSlot()?.templateRef ?? this.topbarActionsTemplate());
  protected readonly sidebarHeaderTpl = computed(() => this.sidebarHeaderSlot()?.templateRef ?? this.sidebarHeaderTemplate());
  protected readonly sidebarTopTpl = computed(() => this.sidebarTopSlot()?.templateRef ?? this.sidebarTopTemplate());
  protected readonly sidebarFooterTpl = computed(() => this.sidebarFooterSlot()?.templateRef ?? this.sidebarFooterTemplate());
  protected readonly mainHeaderTpl = computed(() => this.mainHeaderSlot()?.templateRef ?? this.mainHeaderTemplate());

  /**
   * Extra accordion tabs, forwarded to the menu so IT creates the `<bs-accordion-tab>` elements —
   * the only way they share the generated groups' single-open behavior (see
   * `SparkShellTabDirective`). Data-supplied tabs come first, then projected ones in declaration
   * order.
   */
  private readonly tabSlots = contentChildren(SparkShellTabDirective);

  protected readonly tabs = computed<readonly SparkSidebarTab[]>(() => [
    ...this.sidebarTabs(),
    ...this.tabSlots().map(slot => ({
      header: slot.header(),
      icon: slot.icon(),
      headerTemplate: slot.headerTemplate(),
      content: slot.templateRef,
    })),
  ]);

  protected readonly shellState = signal<BsShellState>('auto');
  protected readonly isSidebarVisible = signal(false);

  // Explicit toggles force show/hide; 'auto' responsive behavior is otherwise preserved by
  // never writing state on our own. The mirror below keeps the toggler icon truthful when the
  // shell opens/closes itself at the breakpoint.
  protected toggleSidebar(open: boolean): void {
    this.shellState.set(open ? 'show' : 'hide');
  }

  protected onShellToggle(detail: ShellStateChangeEventDetail): void {
    this.isSidebarVisible.set(detail.open);
  }
}
