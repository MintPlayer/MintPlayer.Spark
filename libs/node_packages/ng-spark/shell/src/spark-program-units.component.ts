import { ChangeDetectionStrategy, Component, effect, inject, input, signal, untracked } from '@angular/core';
import { DOCUMENT, Location, NgTemplateOutlet } from '@angular/common';
import { IsActiveMatchOptions, Router, RouterLink, RouterLinkActive, UrlTree } from '@angular/router';
import { BsAccordionComponent, BsAccordionTabComponent, BsAccordionTabHeaderDirective } from '@mintplayer/ng-bootstrap/accordion';
import { SPARK_AUTH_STATE } from '@mintplayer/ng-spark';
import { ProgramUnit, ProgramUnitGroup, SparkAppBase, sparkAppUrl } from '@mintplayer/ng-spark/models';
import { SparkService } from '@mintplayer/ng-spark/services';
import { IconNamePipe, ResolveTranslationPipe, RouterLinkPipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';
import { SparkSidebarTab } from './spark-shell-slots';

/** A unit as the menu renders it, classified once when the units load: a router link, or an external `href`. */
interface SparkMenuEntry {
  readonly unit: ProgramUnit;
  /** The router target: a query or persistentObject unit's commands, or an in-app url's tree (query and fragment kept). */
  readonly route?: readonly string[] | UrlTree;
  /** How `routerLinkActive` matches `route`. */
  readonly active?: { exact: boolean } | Partial<IsActiveMatchOptions>;
  /** An external url, as the browser parsed it: never the raw string from the file. */
  readonly href?: string;
}

interface SparkMenuGroup extends Omit<ProgramUnitGroup, 'programUnits'> {
  readonly entries: readonly SparkMenuEntry[];
}

/** A query or persistentObject unit: active on its page and below it, as before url units existed. */
const subset = { exact: false };
/** An in-app url: active on exactly its path, when the page's query holds the url's own, whatever the fragment. */
const exactPath: Partial<IsActiveMatchOptions> = { paths: 'exact', queryParams: 'subset', fragment: 'ignored', matrixParams: 'ignored' };
/** The commands of a query or persistentObject unit: the pipe stays the one mapping, and DI-free. */
const unitCommands = new RouterLinkPipe();

/**
 * The server-driven navigation menu: an accordion of program-unit groups fetched from
 * `GET /spark/program-units`, which the server has already filtered to what the caller's rights
 * allow. Hosts write ZERO router links for navigation — every group, unit, icon, label and link
 * comes from `programUnits.json`; content around the menu belongs in `<spark-shell>`'s slots,
 * and a host tempted to hand-write a unit anchor should add a unit to `programUnits.json`
 * instead.
 *
 * Because the response is caller-scoped it must be re-fetched when the caller changes: the
 * component tracks the optional `SPARK_AUTH_STATE` signal (supplied by ng-spark/auth's
 * `provideSparkAuth()`, or by the app's own auth stack) and reloads on every change. Without a
 * provider it fetches once. `reloadToken` is the manual escape hatch (any changed value triggers
 * a reload), and `reload()` the imperative one.
 *
 * A `url` unit is classified against the application's base (`sparkAppUrl`: the page's origin and the router's base
 * href): a link into the application is a router link in the same tab, any other http(s) link opens in a new tab,
 * and anything else (`javascript:`, `data:`, …) is not rendered at all.
 *
 * Usually rendered by `<spark-shell>`; exported standalone for hosts that own their own layout.
 */
@Component({
  selector: 'spark-program-units',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    NgTemplateOutlet,
    RouterLink, RouterLinkActive,
    BsAccordionComponent, BsAccordionTabComponent, BsAccordionTabHeaderDirective,
    SparkIconComponent, ResolveTranslationPipe, IconNamePipe, TranslateKeyPipe,
  ],
  templateUrl: './spark-program-units.component.html',
  styleUrl: './spark-program-units.component.scss',
})
export class SparkProgramUnitsComponent {
  private readonly sparkService = inject(SparkService);
  private readonly authState = inject(SPARK_AUTH_STATE, { optional: true });
  private readonly router = inject(Router);
  private readonly location = inject(Location);
  private readonly document = inject(DOCUMENT);

  /** Any changed value triggers a reload — for apps whose auth state isn't a provided signal. */
  readonly reloadToken = input<unknown>(null);

  /**
   * Extra tabs to render after the generated groups, normally forwarded by `<spark-shell>` from
   * its `*sparkShellTab` directives. They must be rendered by THIS template — see
   * `SparkShellTabDirective` for why a host-declared `<bs-accordion-tab>` cannot work.
   */
  readonly extraTabs = input<readonly SparkSidebarTab[]>([]);

  protected readonly groups = signal<SparkMenuGroup[]>([]);

  constructor() {
    effect(() => {
      this.authState?.();
      this.reloadToken();
      // Caught: an unloadable menu stays empty rather than rejecting unhandled (fatal to the SSR process).
      untracked(() => this.reload().catch(() => undefined));
    });
  }

  /** Re-fetches the menu. The response is already rights-filtered per caller. */
  async reload(): Promise<void> {
    const config = await this.sparkService.getProgramUnits();
    const base: SparkAppBase = { origin: this.document.location.origin, baseHref: this.location.prepareExternalUrl('/') };
    // Order is the server file's contract, but sort both levels here so the rendered menu never
    // depends on JSON array order — groups were sorted, units were not (#324 F5).
    const groups = [...config.programUnitGroups]
      .sort((a, b) => a.order - b.order)
      .map(({ programUnits, ...group }) => ({
        ...group,
        entries: [...programUnits]
          .sort((a, b) => a.order - b.order)
          .map(unit => this.entry(unit, base))
          .filter((entry): entry is SparkMenuEntry => entry !== null),
      }));
    this.groups.set(groups);
  }

  /** How a unit renders; `null` for a url that is not http(s), which renders nothing. */
  private entry(unit: ProgramUnit, base: SparkAppBase): SparkMenuEntry | null {
    if (unit.type !== 'url') return { unit, route: unitCommands.transform(unit) ?? ['/'], active: subset };
    const target = sparkAppUrl(unit.url, base);
    if (!target) return null;
    return target.kind === 'internal'
      ? { unit, route: this.router.parseUrl(target.route), active: exactPath }
      : { unit, href: target.href };
  }
}
