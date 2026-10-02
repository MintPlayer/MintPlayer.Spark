import { ChangeDetectionStrategy, Component, computed, contentChild, contentChildren, inject, input, signal, TemplateRef } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { BsShellComponent, BsShellSidebarDirective, BsShellState } from '@mintplayer/ng-bootstrap/shell';
import { BsNavbarTogglerComponent } from '@mintplayer/ng-bootstrap/navbar-toggler';
import { BS_THEME_DEFAULT_MODES, BsThemeService, BsThemeToggleComponent, BsThemeToggleMode } from '@mintplayer/ng-bootstrap/theming';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
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
 * Theming: the chrome colors are CSS custom properties —
 * `--spark-shell-{topbar,sidebar,main}-{bg,color}` — declared per colour scheme under
 * `[data-bs-theme=light]` / `[data-bs-theme=dark]` from Bootstrap tokens, so the whole shell follows
 * the page's theme. An app restyles it by overriding those tokens, globally per scheme or on the
 * `<spark-shell>` element.
 *
 * Colour mode: the shell injects `BsThemeService` itself, so 'auto' follows the OS live even when
 * the topbar toggle is hidden with `[themeToggle]="false"` (an app may then place its own
 * `<bs-theme-toggle>` elsewhere). The first paint is themed by `bs-theme-preboot.js` in the app's
 * `index.html`, not by anything here.
 */
@Component({
  selector: 'spark-shell',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    NgTemplateOutlet,
    BsShellComponent, BsShellSidebarDirective, BsNavbarTogglerComponent, BsThemeToggleComponent,
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
   * Render the Auto / Light / Dark `bs-theme-toggle` in the topbar. `false` hides it; the theme
   * service stays live either way.
   */
  readonly themeToggle = input(true);

  /**
   * Instantiated here, not only through the toggle (PRD D3): without it, `[themeToggle]="false"`
   * would leave 'auto' with no live `prefers-color-scheme` listener in Angular's view.
   */
  protected readonly theme = inject(BsThemeService);
  private readonly lang = inject(SparkLanguageService);

  /** The toggle's cycle with Spark's translated labels (`theme.*` keys); icons stay ng-bootstrap's. */
  protected readonly themeModes = computed<readonly BsThemeToggleMode[]>(() =>
    BS_THEME_DEFAULT_MODES.map(m => {
      const name = m.mode.charAt(0).toUpperCase() + m.mode.slice(1);
      return { ...m, label: this.lang.t(`theme.switchTo${name}`), announcement: this.lang.t(`theme.${m.mode}`) };
    }));

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
