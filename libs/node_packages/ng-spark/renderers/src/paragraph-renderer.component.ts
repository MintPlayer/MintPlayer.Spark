import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { SparkAttributeDetailRenderer } from './spark-attribute-renderer';

/**
 * The core `paragraph` renderer (generic passkeys page, D5): a read-only string shown as body text,
 * full width and without a label — an explanatory text block on a persistent object, filled from
 * translations by the actions class. Pair it with `showedOn: PersistentObject` so it never becomes a
 * grid column.
 *
 * **Escaped by default.** The value is interpolated, so markup in it is shown as text. A blank line
 * starts a new paragraph and a single newline is a line break.
 *
 * `"rendererOptions": { "sanitize": false }` renders the value as HTML instead, through Angular's
 * `[innerHTML]`, whose sanitizer still strips scripts, event handlers and `javascript:` URLs. There is
 * deliberately no `bypassSecurityTrustHtml` anywhere on this path: the opt-out is "allow markup",
 * never "allow script".
 */
@Component({
  selector: 'spark-paragraph-renderer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (asHtml()) {
      <div class="spark-paragraph" [innerHTML]="text()"></div>
    } @else {
      @for (paragraph of paragraphs(); track $index) {
        <p class="spark-paragraph">@for (line of paragraph; track $index) {@if (!$first) {<br />}{{ line }}}</p>
      }
    }
  `,
  styles: [`
    :host { display: block; }
    .spark-paragraph:last-child { margin-bottom: 0; }
  `],
})
export class SparkParagraphRendererComponent implements SparkAttributeDetailRenderer {
  readonly value = input<any>();
  readonly options = input<Record<string, any> | undefined>();

  protected readonly text = computed(() => {
    const value = this.value();
    return value === null || value === undefined ? '' : String(value);
  });

  /** Only an explicit `false` opts out of escaping; anything else, absent included, escapes. */
  protected readonly asHtml = computed(() => this.options()?.['sanitize'] === false);

  /** Paragraphs split on blank lines, each a list of its lines. */
  protected readonly paragraphs = computed(() => {
    const text = this.text().replace(/\r\n?/g, '\n').trim();
    if (!text) return [];
    return text.split(/\n[ \t]*\n+/).map(paragraph => paragraph.split('\n'));
  });
}
