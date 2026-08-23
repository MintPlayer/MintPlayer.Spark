import { Directive, inject, input, TemplateRef } from '@angular/core';
import { CustomActionDefinition, PersistentObject, SparkQuery } from '@mintplayer/ng-spark/models';

/**
 * Header slots for `<spark-query-card>`.
 *
 * Each directive marks a template that REPLACES one region of the card header. Supplying one
 * leaves the other two alone — that is the whole point of having three rather than the single
 * whole-header template they replace, where changing the icon meant re-implementing the caption
 * and the action bar as well.
 *
 * An omitted slot is not an empty slot: the card renders its default. This is what keeps the
 * auto-rendered sub-query working. A sub-query is rendered from `EntityTypeDefinition.Queries`
 * with no host to project into, so a mechanism that only ever REPLACED chrome would leave that
 * call site with nothing — which is why an earlier design had the query declare its own chrome
 * server-side. Overriding a default needs no host; replacing one does.
 *
 * The two ends of that are visible in the defaults themselves. Neither `SparkQuery` nor
 * `EntityType` carries an icon, so the server has nothing to say and the slot is the only
 * source. Actions are the mirror image: the server declares them per type, so the default is
 * the real answer and the slot is a rare override.
 *
 * Naming follows ng-bootstrap's `*bsDatatableColumn` convention — prefix, component, slot —
 * with `spark`, since these are ng-spark directives and `bs` is another package's prefix. These
 * are the first attribute directives in ng-spark; every existing selector is an element.
 *
 * ## Targeting one query
 *
 * Each slot takes an optional query alias or id as its value. A detail page renders one card per
 * entry in `EntityTypeDefinition.Queries`, so a host that supplies a bare slot would decorate
 * every one of them identically — rarely what is wanted. A targeted slot applies to that query;
 * an untargeted one is the fallback for the rest.
 *
 * ```html
 * <span *sparkQueryIcon="'cars'"><spark-icon name="car-front" /></span>
 * <span *sparkQueryIcon><spark-icon name="table" /></span>
 * ```
 *
 * Matching is case-insensitive, against the query's alias and then its id. This is the
 * `bsPriorityNavItem` shape: a value input aliased to the selector, collected with
 * `contentChildren`.
 */

/** Resolves a slot list to the one that applies to a query: targeted first, then untargeted. */
export function slotFor<T extends { forQuery: () => string }>(
  slots: readonly T[],
  query: SparkQuery | null,
): T | null {
  if (!slots.length) return null;

  const matches = (target: string) =>
    !!target && (
      target.localeCompare(query?.alias ?? '', undefined, { sensitivity: 'accent' }) === 0 ||
      target.localeCompare(query?.id ?? '', undefined, { sensitivity: 'accent' }) === 0);

  // A targeted slot wins over the catch-all even when it is declared later: "this one query"
  // is the more specific statement, and declaration order in a template is not a priority.
  return slots.find(s => matches(s.forQuery())) ?? slots.find(s => !s.forQuery()) ?? null;
}

/**
 * The icon at the header's leading edge. No default: nothing in the model describes one.
 *
 * ```html
 * <spark-query-card [queryId]="'cars'">
 *   <spark-icon *sparkQueryIcon name="car-front" />
 * </spark-query-card>
 * ```
 */
@Directive({ selector: '[sparkQueryIcon]' })
export class SparkQueryIconDirective {
  readonly templateRef = inject<TemplateRef<SparkQuerySlotContext>>(TemplateRef);

  /** Query alias or id this slot applies to. Empty targets every query on the page. */
  readonly forQuery = input('', { alias: 'sparkQueryIcon' });

  static ngTemplateContextGuard(
    _dir: SparkQueryIconDirective,
    ctx: unknown,
  ): ctx is SparkQuerySlotContext {
    return true;
  }
}

/**
 * The header caption. Defaults to the query's translated description, falling back to its name.
 *
 * The context carries that resolved default as `$implicit`, so a host can decorate it — add a
 * count, a badge — without re-resolving the translation itself.
 */
@Directive({ selector: '[sparkQueryCaption]' })
export class SparkQueryCaptionDirective {
  readonly templateRef = inject<TemplateRef<SparkQueryCaptionContext>>(TemplateRef);

  readonly forQuery = input('', { alias: 'sparkQueryCaption' });

  static ngTemplateContextGuard(
    _dir: SparkQueryCaptionDirective,
    ctx: unknown,
  ): ctx is SparkQueryCaptionContext {
    return true;
  }
}

/**
 * The buttons at the header's trailing edge. Defaults to the server-declared custom actions.
 *
 * The context carries those actions and the current selection, so a host that wants its own
 * buttons ALONGSIDE the server's can render both rather than choosing. Without that, overriding
 * this slot to add one button would silently drop every action the type declares — and the
 * server's actions are the ones carrying `selectionRule` and the permission filter.
 *
 * ```html
 * <ng-container *sparkQueryActions="let actions; selection as rows">
 *   <button (click)="export(rows)">Export</button>
 * </ng-container>
 * ```
 */
@Directive({ selector: '[sparkQueryActions]' })
export class SparkQueryActionsDirective {
  readonly templateRef = inject<TemplateRef<SparkQueryActionsContext>>(TemplateRef);

  readonly forQuery = input('', { alias: 'sparkQueryActions' });

  static ngTemplateContextGuard(
    _dir: SparkQueryActionsDirective,
    ctx: unknown,
  ): ctx is SparkQueryActionsContext {
    return true;
  }
}

/** Context for the icon slot: the query whose card is being rendered. */
export class SparkQuerySlotContext {
  $implicit: SparkQuery | null = null;
}

export class SparkQueryCaptionContext {
  /** The caption the card would have rendered: description, or the query name. */
  $implicit = '';
  query: SparkQuery | null = null;
}

export class SparkQueryActionsContext {
  /** The custom actions the card would have rendered, already filtered for this query. */
  $implicit: CustomActionDefinition[] = [];
  /** Rows currently ticked. Empty unless an action is selection-gated. */
  selection: PersistentObject[] = [];
  query: SparkQuery | null = null;
}
