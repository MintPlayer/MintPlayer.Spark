import { inject, Injectable, Type } from '@angular/core';
import {
  EntityAttributeDefinition,
  LookupReference,
  PersistentObject,
} from '@mintplayer/ng-spark/models';
import { SPARK_ATTRIBUTE_RENDERERS, rendererValue, withDeclaredInputs } from '@mintplayer/ng-spark/renderers';
import { SparkService } from '@mintplayer/ng-spark/services';

/**
 * The parts of a Spark grid that both grid components need and that were, until this existed,
 * written out twice.
 *
 * `spark-query-list` and `spark-sub-query` had byte-identical copies of the renderer lookup, the
 * renderer input construction and the lookup-reference loading — around 120 lines between them.
 * That duplication is not a tidiness complaint: it is what produced the drift. The two copies
 * disagreed about `[indeterminate]`, about resetting permission state, about whether a fetch
 * failure surfaces or is swallowed, and about virtual-scroll sizing — four user-visible bugs, each
 * fixed on one side and not the other, because nothing made the two files move together.
 *
 * Kept deliberately small and stateless. The two components differ in real ways — one is
 * route-coupled and carries streaming, search and a websocket dependency graph — so merging them
 * into a single component would drag all of that into every detail page's bundle. Shared logic
 * belongs here; shared *state* does not.
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
    return withDeclaredInputs(component, {
      value: rendererValue(itemAttr),
      attribute: attr,
      options: attr.rendererOptions,
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
