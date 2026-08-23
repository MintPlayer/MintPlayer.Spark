import { ChangeDetectionStrategy, Component, computed, contentChildren, input, output, viewChild, TemplateRef } from '@angular/core';
import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsPriorityNavComponent, BsPriorityNavItemDirective } from '@mintplayer/ng-bootstrap/priority-nav';
import { ResolveTranslationPipe } from '@mintplayer/ng-spark/pipes';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { CustomActionDefinition, PersistentObject } from '@mintplayer/ng-spark/models';
import { inject } from '@angular/core';
import { SparkQueryGridComponent } from './spark-query-grid.component';
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
  imports: [CommonModule, BsCardComponent, BsCardHeaderComponent, BsPriorityNavComponent, BsPriorityNavItemDirective, SparkQueryGridComponent, ResolveTranslationPipe],
  templateUrl: './spark-query-card.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkQueryCardComponent {
  readonly lang = inject(SparkLanguageService);

  queryId = input.required<string>();
  parentId = input<string>('');
  parentType = input<string>('');
  reloadToken = input<unknown>(null);
  data = input<PersistentObject[] | null>(null);
  search = input<string>('');

  /**
   * Slots forwarded from a host that cannot project content — see the class comment. A card
   * reached through hand-written markup should use the structural directives instead.
   */
  iconTemplate = input<TemplateRef<any> | null>(null);
  captionTemplate = input<TemplateRef<any> | null>(null);
  actionsTemplate = input<TemplateRef<any> | null>(null);

  error = output<HttpErrorResponse>();
  rowClicked = output<PersistentObject>();
  customActionExecuted = output<{ action: CustomActionDefinition }>();

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
    return (q?.description ? this.lang.resolve(q.description) : '') || q?.name || '';
  });

  protected readonly customActions = computed(() => this.grid()?.customActions() ?? []);
  protected readonly selection = computed(() => this.grid()?.selection() ?? []);
}
