import { ChangeDetectionStrategy, Component, effect, inject, input, signal, untracked } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { BsAccordionComponent, BsAccordionTabComponent, BsAccordionTabHeaderDirective } from '@mintplayer/ng-bootstrap/accordion';
import { SPARK_AUTH_STATE } from '@mintplayer/ng-spark';
import { ProgramUnitGroup } from '@mintplayer/ng-spark/models';
import { SparkService } from '@mintplayer/ng-spark/services';
import { IconNamePipe, ResolveTranslationPipe, RouterLinkPipe } from '@mintplayer/ng-spark/pipes';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';

/**
 * The server-driven navigation menu: an accordion of program-unit groups fetched from
 * `GET /spark/program-units`, which the server has already filtered to what the caller's rights
 * allow. Hosts write ZERO router links for navigation — every group, unit, icon, label and link
 * comes from `programUnits.json`; content around the menu belongs in `<spark-shell>`'s slots,
 * and a host tempted to hand-write a unit anchor should add a unit to `programUnits.json`
 * instead.
 *
 * Because the response is caller-scoped it must be re-fetched when the caller changes: the
 * component tracks the optional `SPARK_AUTH_STATE` signal (supplied by ng-spark-auth's
 * `provideSparkAuth()`, or by the app's own auth stack) and reloads on every change. Without a
 * provider it fetches once. `reloadToken` is the manual escape hatch (any changed value triggers
 * a reload), and `reload()` the imperative one.
 *
 * Usually rendered by `<spark-shell>`; exported standalone for hosts that own their own layout.
 */
@Component({
  selector: 'spark-program-units',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink, RouterLinkActive,
    BsAccordionComponent, BsAccordionTabComponent, BsAccordionTabHeaderDirective,
    SparkIconComponent, ResolveTranslationPipe, IconNamePipe, RouterLinkPipe,
  ],
  templateUrl: './spark-program-units.component.html',
  styleUrl: './spark-program-units.component.scss',
})
export class SparkProgramUnitsComponent {
  private readonly sparkService = inject(SparkService);
  private readonly authState = inject(SPARK_AUTH_STATE, { optional: true });

  /** Any changed value triggers a reload — for apps whose auth state isn't a provided signal. */
  readonly reloadToken = input<unknown>(null);

  protected readonly groups = signal<ProgramUnitGroup[]>([]);

  constructor() {
    effect(() => {
      this.authState?.();
      this.reloadToken();
      untracked(() => this.reload());
    });
  }

  /** Re-fetches the menu. The response is already rights-filtered per caller. */
  async reload(): Promise<void> {
    const config = await this.sparkService.getProgramUnits();
    // Order is the server file's contract, but sort both levels here so the rendered menu never
    // depends on JSON array order — groups were sorted, units were not (#324 F5).
    const groups = [...config.programUnitGroups]
      .sort((a, b) => a.order - b.order)
      .map(g => ({ ...g, programUnits: [...g.programUnits].sort((a, b) => a.order - b.order) }));
    this.groups.set(groups);
  }
}
