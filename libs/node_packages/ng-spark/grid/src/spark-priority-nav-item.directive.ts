import { computed, Directive, inject, input, Signal, TemplateRef } from '@angular/core';
import type { PriorityNavItem } from '@mintplayer/ng-bootstrap/priority-nav';

/**
 * One item of a Spark toolbar's `<bs-priority-nav>`, written in place as a template:
 *
 * ```html
 * <bs-priority-nav [items]="navItems()">
 *   <ng-container *bsPriorityNavItemTemplate="let item"><ng-container [ngTemplateOutlet]="$any(item).template" /></ng-container>
 *   <button *sparkPriorityNavItem="1" (click)="onBack()">Back</button>
 * </bs-priority-nav>
 * ```
 *
 * Since ng-bootstrap 22.33 the priority nav takes its items as data (`[items]`) and renders each with
 * one item template. Spark's toolbars mix buttons, `@for` loops and slot outlets, so this directive keeps
 * the old "mark the element, give it a priority" shape: it only captures its template, the host collects
 * the directives with `viewChildren` and hands them to the nav through {@link sparkPriorityNavItems}.
 * The template keeps its declaration context, so loop variables still resolve.
 */
@Directive({ selector: '[sparkPriorityNavItem]' })
export class SparkPriorityNavItemDirective {
  private static nextKey = 0;

  readonly template = inject<TemplateRef<unknown>>(TemplateRef);
  /** Lower is more important; null overflows first. */
  readonly priority = input<number | null>(null, { alias: 'sparkPriorityNavItem' });
  /** Stable across renders, so the nav keeps each item's measurement while the directive lives. */
  readonly key = `spark-nav-item-${SparkPriorityNavItemDirective.nextKey++}`;
}

/** A priority-nav item carrying the template to render. */
export interface SparkPriorityNavEntry extends PriorityNavItem {
  template: TemplateRef<unknown>;
}

/** The `[items]` of a `<bs-priority-nav>` from the `*sparkPriorityNavItem` directives a view holds, in template order. */
export function sparkPriorityNavItems(directives: Signal<readonly SparkPriorityNavItemDirective[]>): Signal<SparkPriorityNavEntry[]> {
  return computed(() => directives().map(d => ({ key: d.key, priority: d.priority(), template: d.template })));
}
