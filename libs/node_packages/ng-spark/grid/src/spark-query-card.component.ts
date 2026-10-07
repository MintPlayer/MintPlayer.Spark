import { ChangeDetectionStrategy, Component, computed, contentChildren, input, linkedSignal, output, viewChild, TemplateRef } from '@angular/core';
import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsPriorityNavComponent, BsPriorityNavItemDirective } from '@mintplayer/ng-bootstrap/priority-nav';
import { ResolveTranslationPipe } from '@mintplayer/ng-spark/pipes';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { CustomActionDefinition, PersistentObject, QueryResultItem, type SparkDeletedFilter, type SparkSelectionModeSetting } from '@mintplayer/ng-spark/models';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';
import { SparkQueryToolbarAction, sparkActionClass } from './spark-query-toolbar';
import { inject } from '@angular/core';
import { SparkQueryGridComponent } from './spark-query-grid.component';
import { SparkSearchBoxComponent } from './spark-search-box.component';
import {
  SparkQueryActionsDirective,
  SparkQueryCaptionDirective,
  SparkQueryIconDirective,
  slotFor,
} from './spark-query-slots';

/**
 * A `<bs-card>` around a {@link SparkQueryGridComponent}: icon, caption, actions, grid.
 *
 * ## Chrome is overridden, not replaced
 *
 * Every header region has a default, and a host replaces only the ones it supplies. That is what
 * lets this component serve the auto-rendered case: a sub-query is rendered once per entry in
 * `EntityTypeDefinition.Queries` with nobody projecting into it, and it must look right with no
 * host cooperation at all. A slot mechanism that only ever *replaced* chrome would leave that
 * call site blank — which is why an earlier design had the query carry its own chrome from the
 * server. Overriding a default needs no host; replacing one does.
 *
 * ## Two ways in, one set of templates
 *
 * Hand-written markup uses the structural directives and is found with `contentChildren`.
 * The auto-rendered path cannot: `spark-po-detail` is created by the router, so in a default app
 * there is no tag to project into. It therefore forwards `TemplateRef`s as inputs instead — a
 * directive cannot cross a component boundary but its template can, and that forwarding is
 * already the idiom in `spark-po-detail` (`extraActionsTemplate`, `extraContentTemplate`).
 *
 * Content wins over a forwarded input: the closer declaration is the more specific one.
 */
@Component({
  selector: 'spark-query-card',
  imports: [CommonModule, BsCardComponent, BsCardHeaderComponent, BsPriorityNavComponent, BsPriorityNavItemDirective, SparkQueryGridComponent, SparkSearchBoxComponent, ResolveTranslationPipe, SparkIconComponent],
  // The search box keeps a steady width beside the actions; the priority nav gives way first.
  //
  // Below `sm` the nav has nothing left to give: `collapseAt="sm"` folds every action into "More",
  // and that toggle never shrinks. Caption + "More" + the box's 8rem minimum then overran a 375 px
  // header by ~11 px, which the card's `overflow: hidden` clipped. So below `sm` the header wraps,
  // the box drops to its own line when it does not fit, and there it takes the full width.
  styles: [`
    .spark-query-card-search { flex: 0 1 14rem; min-width: 8rem; }
    @media (max-width: 575.98px) {
      .spark-query-card-header { flex-wrap: wrap; }
      .spark-query-card-search { flex: 1 1 8rem; }
    }
  `],
  templateUrl: './spark-query-card.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkQueryCardComponent {
  readonly lang = inject(SparkLanguageService);

  queryId = input.required<string>();
  parentId = input<string>('');
  parentType = input<string>('');
  reloadToken = input<unknown>(null);
  data = input<QueryResultItem[] | null>(null);

  /**
   * The initial search term. The header's search box starts from it and the user takes over from
   * there; a new value from the host replaces what was typed.
   */
  search = input<string>('');

  /**
   * Whether the header offers the search box (#460 M15, D17 addendum). On by default, as every
   * Vidyano sub-query tab has one; a card over bound `data` never shows it, because the grid does
   * not refetch bound rows and the box would do nothing.
   */
  searchable = input<boolean>(true);

  /** What the grid searches: the host's `search`, until the user types in the header box. */
  readonly searchTerm = linkedSignal(() => this.search());

  protected readonly showSearch = computed(() => this.searchable() && this.data() === null);

  /**
   * Overrides the query's `selectionMode` for this card (#460, D17) — the detail page passes the
   * sub-query entry's. `null` defers to the query (absent there = `'auto'`).
   */
  selectionMode = input<SparkSelectionModeSetting | null>(null);

  /**
   * Soft-deletion mode and deleted parent (#460), forwarded to the grid. In the recycle bin — the
   * grid lists `deleted: 'only'`, or its parent row was opened with `?deleted=only` — the card offers
   * no New, Delete or custom action, exactly as the query-list page does. See `recycleBin`.
   */
  deleted = input<SparkDeletedFilter | null | undefined>(null);
  parentDeleted = input(false);

  /**
   * Slots forwarded from a host that cannot project content — see the class comment. A card
   * reached through hand-written markup should use the structural directives instead.
   */
  iconTemplate = input<TemplateRef<any> | null>(null);
  captionTemplate = input<TemplateRef<any> | null>(null);
  actionsTemplate = input<TemplateRef<any> | null>(null);

  error = output<HttpErrorResponse>();
  rowClicked = output<QueryResultItem>();
  customActionExecuted = output<{ action: CustomActionDefinition }>();

  /**
   * Forwarded to the grid. Without this the escape hatch was unreachable from a card — i.e. from
   * every auto-rendered sub-query and program-unit query page, which is most grids.
   */
  rowRoute = input<((row: QueryResultItem) => unknown[] | null) | null>(null);

  colors = Color;

  private readonly iconSlots = contentChildren(SparkQueryIconDirective);
  private readonly captionSlots = contentChildren(SparkQueryCaptionDirective);
  private readonly actionSlots = contentChildren(SparkQueryActionsDirective);

  /**
   * The grid, read only to surface its state — the query, the actions, the selection — to this
   * component's own header.
   *
   * Optional rather than `required` on purpose. The header renders ABOVE the grid in this
   * template, so on the first change-detection pass the view query has not resolved yet and
   * `viewChild.required()` would throw where a plain one returns `undefined`. Everything below
   * therefore reads it defensively; the signal updates once the view initialises and the header
   * re-renders with the real query.
   *
   * A host reads grid state through a template reference variable instead, because a host may put
   * the grid behind an `@if`, where a view query genuinely is intermittently undefined.
   */
  protected readonly grid = viewChild(SparkQueryGridComponent);

  protected readonly query = computed(() => this.grid()?.query() ?? null);

  protected readonly iconTpl = computed(() =>
    slotFor(this.iconSlots(), this.query())?.templateRef ?? this.iconTemplate());

  protected readonly captionTpl = computed(() =>
    slotFor(this.captionSlots(), this.query())?.templateRef ?? this.captionTemplate());

  protected readonly actionsTpl = computed(() =>
    slotFor(this.actionSlots(), this.query())?.templateRef ?? this.actionsTemplate());

  /** The caption the card renders unless a slot replaces it. */
  protected readonly caption = computed(() => {
    const q = this.query();
    return (q?.label ? this.lang.resolve(q.label) : '') || q?.name || '';
  });

  protected readonly customActions = computed(() => this.grid()?.visibleCustomActions() ?? []);

  /**
   * The header's buttons: New, Delete and the custom actions, from the grid (#460, M15) — the same
   * list the query-list page renders.
   */
  protected readonly toolbarActions = computed(() => this.grid()?.toolbarActions() ?? []);

  protected actionClass(action: SparkQueryToolbarAction): string {
    // Square corners in the card header (owner, 2026-09-29): the buttons sit edge to edge there.
    return `${sparkActionClass(action.definition, 'sm')} rounded-0`;
  }

  protected isEnabled(action: SparkQueryToolbarAction): boolean {
    return this.grid()?.isToolbarActionEnabled(action) ?? false;
  }

  /** The tooltip of an action disabled for want of its client method (`requiresClient`); null otherwise. */
  protected unavailableReason(action: SparkQueryToolbarAction): string | null {
    return this.grid()?.actionUnavailableReason(action.definition) ?? null;
  }

  protected run(action: SparkQueryToolbarAction): void {
    void this.grid()?.runToolbarAction(action);
  }
  protected readonly selection = computed(() => this.grid()?.selection() ?? []);
}
