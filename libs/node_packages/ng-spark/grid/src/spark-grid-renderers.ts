import { inject, Injectable, Type } from '@angular/core';
import {
  EntityAttributeDefinition,
  LookupReference,
  PersistentObject,
} from '@mintplayer/ng-spark/models';
import { SPARK_ATTRIBUTE_RENDERERS, rendererValue, withDeclaredInputs } from '@mintplayer/ng-spark/renderers';
import { SparkService } from '@mintplayer/ng-spark/services';

/**
 * Renderer lookup and lookup-reference loading for a Spark grid.
 *
 * This was extracted when `spark-query-list` and `spark-sub-query` were two components holding
 * byte-identical copies of it — around 120 lines between them. That duplication was not a
 * tidiness complaint: it produced drift. The two copies disagreed about `[indeterminate]`, about
 * resetting permission state, about whether a fetch failure surfaces or is swallowed, and about
 * virtual-scroll sizing — four user-visible bugs, each fixed on one side and not the other.
 *
 * There is now one grid, {@link SparkQueryGridComponent}, so the drift cannot recur. This stays
 * separate because it is stateless service-shaped logic rather than view state, and because it is
 * what a custom grid would need in order to render Spark cells at all.
 *
 * The constraint that shaped the split still holds and is why the grid takes rows as an input
 * rather than owning a socket: streaming's dependency graph must not reach the bundle of every
 * detail page.
 */
@Injectable({ providedIn: 'root' })
export class SparkGridRenderers {
  private readonly registry = inject(SPARK_ATTRIBUTE_RENDERERS);
  private readonly sparkService = inject(SparkService);

  /** The registered column component for an attribute, or null to fall back to the default cell. */
  columnComponentFor(attr: EntityAttributeDefinition): Type<any> | null {
    if (!attr.renderer) return null;
    return this.registry.find(r => r.name === attr.renderer)?.columnComponent ?? null;
  }

  /**
   * Inputs for a column renderer, filtered to what the component actually declares —
   * `NgComponentOutlet` throws on an input the target does not have, which is what lets every
   * member of the renderer contract be optional.
   */
  columnInputsFor(component: Type<any>, item: PersistentObject, attr: EntityAttributeDefinition): Record<string, any> {
    const itemAttr = item.attributes.find(a => a.name === attr.name);
    return this.cellInputsFor(component, rendererValue(itemAttr), attr, item);
  }

  /**
   * The same contract for a row that is not a <see cref="PersistentObject"/>.
   *
   * An AsDetail row is a plain dictionary — embedded values with no id and no attribute list — so
   * its value cannot be read the way {@link columnInputsFor} reads one. Only the extraction
   * differs; the renderer contract is identical, and stating it once is what stops the two from
   * drifting the way the cell markup already had.
   */
  cellInputsFor(component: Type<any>, value: unknown, column: EntityAttributeDefinition, item: unknown): Record<string, any> {
    return withDeclaredInputs(component, {
      value,
      attribute: column,
      options: column.rendererOptions,
      item,
    });
  }

  /**
   * Loads every lookup reference the visible attributes need, in one pass.
   *
   * Returns an empty map rather than throwing when there are none, so callers never branch on it.
   */
  async loadLookupOptions(attributes: EntityAttributeDefinition[]): Promise<Record<string, LookupReference>> {
    const lookupAttrs = attributes.filter(a => a.lookupReferenceType);
    if (lookupAttrs.length === 0) return {};

    const names = [...new Set(lookupAttrs.map(a => a.lookupReferenceType!))];
    const entries = await Promise.all(
      names.map(async name => [name, await this.sparkService.getLookupReference(name)] as const),
    );
    return entries.reduce((acc, [k, v]) => ({ ...acc, [k]: v }), {} as Record<string, LookupReference>);
  }
}
