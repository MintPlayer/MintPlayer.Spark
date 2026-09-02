import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { BsTooltipDirective } from '@mintplayer/ng-bootstrap/tooltip';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';
import { TranslatedString, currentLanguage, resolveTranslation } from '@mintplayer/ng-spark/models';

export type SparkAttributeDescriptionPosition = 'top' | 'bottom' | 'start' | 'end';

/**
 * The [i] beside an attribute label: a focusable button whose tooltip shows the attribute's
 * `description` (#348). Renders nothing when the model declares no description, so every label
 * site can include it unconditionally.
 *
 * Accessibility comes from `*bsTooltip`: it opens on hover AND focus, closes on Escape, and sets
 * `aria-describedby` on the button while open. The button's own name is the description text, so
 * a screen reader announces the help on focus without waiting for the tooltip.
 *
 * Clicks are stopped: the [i] lives inside sortable grid headers and `<label for>` elements, and
 * must neither toggle the sort nor move focus into the input.
 */
@Component({
  selector: 'spark-attribute-description',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BsTooltipDirective, SparkIconComponent],
  template: `
    @if (text(); as text) {
      <button
        type="button"
        class="btn btn-link p-0 ms-1 align-baseline spark-attribute-description-trigger"
        [attr.aria-label]="text"
        (click)="onClick($event)">
        <spark-icon name="info-circle" />
        <span *bsTooltip="position()" class="spark-attribute-description-text">{{ text }}</span>
      </button>
    }
  `,
  styles: [`
    :host {
      display: inline-block;
      line-height: 1;
    }
    .spark-attribute-description-trigger {
      color: inherit;
      opacity: 0.6;
      font-size: 0.875em;
      vertical-align: baseline;
    }
    .spark-attribute-description-trigger:hover,
    .spark-attribute-description-trigger:focus-visible {
      opacity: 1;
    }
    .spark-attribute-description-text {
      display: inline-block;
      white-space: pre-line;
      text-align: start;
      max-width: 20rem;
    }
  `],
})
export class SparkAttributeDescriptionComponent {
  /** The attribute's `description`; `undefined` (the common case) renders nothing. */
  description = input<TranslatedString | undefined>();

  /** Where the tooltip opens relative to the [i]. */
  position = input<SparkAttributeDescriptionPosition>('top');

  /** Resolved for the current language; re-evaluates when the user switches language. */
  text = computed(() => resolveTranslation(this.description(), currentLanguage()).trim());

  onClick(event: MouseEvent): void {
    event.preventDefault();
    event.stopPropagation();
  }
}
