import { Injectable, inject } from '@angular/core';
import { CustomActionDefinition, PersistentObject, SparkQuery, EntityType, filterQueryActions } from '@mintplayer/ng-spark/models';
import { SparkService } from './spark.service';

/**
 * A query's custom actions, reachable **without the grid that usually renders them**.
 *
 * ## Why this exists
 *
 * A query's actions were only ever obtainable by rendering `<spark-query-grid>`, which loads the
 * query, resolves its entity type, fetches the actions and filters them to the ones marked for a
 * query surface — all privately. A page that wanted the same buttons somewhere else (a toolbar
 * above the card, the shell's topbar) had three bad options: duplicate the four-step resolution
 * and let it drift, reach into the grid's internals, or put the button in the wrong place.
 *
 * This exposes the resolution itself. The grid keeps rendering the default placement; a host that
 * wants a different one asks here and renders its own control.
 *
 * ## What it does not do
 *
 * It does not authorize. `/spark/actions/{type}` returns only the actions this caller may see —
 * the server filters against `security.json`, and executing re-checks — so nothing here is a gate,
 * and a host must not treat "the list came back empty" as anything other than a display fact.
 *
 * It also does not cache. Actions depend on the caller, and a memo keyed by type id would survive
 * a sign-out.
 */
@Injectable({ providedIn: 'root' })
export class SparkQueryActionsService {
  private readonly sparkService = inject(SparkService);

  /**
   * The custom actions of the query named by id or alias, already filtered to those that belong on
   * a query surface.
   *
   * Returns an empty list — rather than throwing — when the query resolves to no entity type,
   * because a caller rendering a toolbar wants "no buttons", not a broken page.
   */
  async actionsFor(queryIdOrAlias: string): Promise<CustomActionDefinition[]> {
    const context = await this.contextFor(queryIdOrAlias);
    if (!context) return [];

    // filterQueryActions, not a local predicate: the grid uses it too, and the `showedOn` values
    // are exactly the kind of thing a second copy gets subtly wrong — both grids once tested for
    // "list", a value nothing emits, and every correctly authored action rendered nowhere.
    return filterQueryActions(await this.sparkService.getCustomActions(context.entityType.id));
  }

  /**
   * Runs one of those actions. `selectedItemIds` are row ids, exactly as the grid posts them —
   * the server re-materializes each one through the same load path a detail page uses, so an id
   * from anywhere is treated as caller input rather than as a verified row.
   */
  async execute(
    queryIdOrAlias: string,
    actionName: string,
    options?: { parent?: PersistentObject; selectedItemIds?: string[] },
  ): Promise<void> {
    const context = await this.contextFor(queryIdOrAlias);
    if (!context) {
      throw new Error(
        `Cannot execute '${actionName}': query '${queryIdOrAlias}' resolves to no entity type, so there ` +
        `is nothing to execute it against.`);
    }

    await this.sparkService.executeCustomAction(
      context.entityType.id, actionName, options?.parent, options?.selectedItemIds);
  }

  /**
   * The query and the entity type its rows are mapped against — the two-step resolution both
   * methods need, kept in one place so they cannot disagree about which type an action runs on.
   */
  private async contextFor(
    queryIdOrAlias: string,
  ): Promise<{ query: SparkQuery; entityType: EntityType } | null> {
    const [query, entityTypes] = await Promise.all([
      this.sparkService.getQuery(queryIdOrAlias),
      this.sparkService.getEntityTypes(),
    ]);
    if (!query) return null;

    const entityType = entityTypes.find(t => t.name === query.entityType)
      ?? entityTypes.find(t => t.id === query.entityType);
    return entityType ? { query, entityType } : null;
  }
}
