import { ChangeDetectionStrategy, Component, DestroyRef, effect, inject, input, model, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsInputGroupComponent } from '@mintplayer/ng-bootstrap/input-group';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';

/** How long typing must pause before the term is published, in milliseconds. */
export const SPARK_SEARCH_DEBOUNCE_MS = 300;

/**
 * The query search box: a magnifier, the input and a clear button (#460 M15, D17 addendum).
 *
 * One component for both surfaces that search a query — the query-list page above its grid and the
 * `<spark-query-card>` header next to its actions, as Vidyano's sub-query tabs have it — so they
 * behave identically: the same debounce, the same clear button, the same placeholder.
 *
 * Two-way bound through `term`, which is what a grid's `search` input takes:
 *
 * ```html
 * <spark-search-box [(term)]="searchTerm" />
 * <spark-query-grid [search]="searchTerm()" … />
 * ```
 *
 * Typing publishes the term once it has paused for {@link SPARK_SEARCH_DEBOUNCE_MS}, so a word is
 * one query rather than one per keystroke. Clearing (the ✕ button, or Escape) publishes at once. A
 * `term` set from outside — a host resetting it — replaces what the box shows.
 */
@Component({
  selector: 'spark-search-box',
  imports: [FormsModule, BsFormComponent, BsFormControlDirective, BsInputGroupComponent, SparkIconComponent, TranslateKeyPipe],
  template: `
    <bs-form>
      <bs-input-group [size]="size()">
        <span class="addon">
          <spark-icon name="search" />
        </span>
        <input
          type="text"
          class="spark-search-input"
          [attr.aria-label]="'common.search' | t"
          [placeholder]="'common.search' | t"
          [ngModel]="text()"
          (ngModelChange)="onInput($event)"
          (keydown.escape)="clear()">
        @if (text()) {
          <button
            type="button"
            class="btn btn-outline-secondary spark-search-clear"
            [attr.aria-label]="'common.clearSearch' | t"
            (click)="clear()">
            <spark-icon name="x-lg" />
          </button>
        }
      </bs-input-group>
    </bs-form>
  `,
  styles: [`
    :host { display: block; }
    /* bs-form renders a <form>; in a card header it must not add the page form's spacing. */
    :host ::ng-deep form { margin: 0; }
  `],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkSearchBoxComponent {
  /** The published search term. Debounced while typing; immediate on clear. */
  readonly term = model<string>('');

  /** The input group's size: `sm` for a card header, the default for a page. */
  readonly size = input<'sm' | 'md' | 'lg'>('md');

  /** Overrides {@link SPARK_SEARCH_DEBOUNCE_MS}; `0` publishes on every keystroke. */
  readonly debounce = input<number>(SPARK_SEARCH_DEBOUNCE_MS);

  /** What the input shows, which runs ahead of `term` while the debounce is pending. */
  protected readonly text = signal('');

  private timer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    // A term set from outside replaces the text. Reading only `term` keeps typing (which writes
    // `text` first and `term` later) from bouncing back into the box mid-word.
    effect(() => {
      const term = this.term();
      untracked(() => {
        if (term !== this.text()) {
          this.cancelPending();
          this.text.set(term);
        }
      });
    });

    inject(DestroyRef).onDestroy(() => this.cancelPending());
  }

  protected onInput(value: string): void {
    this.text.set(value ?? '');
    this.cancelPending();

    const delay = this.debounce();
    if (delay <= 0 || !value) {
      // Emptying the box by hand is a clear: publish at once, like the button.
      this.term.set(value ?? '');
      return;
    }
    this.timer = setTimeout(() => {
      this.timer = null;
      this.term.set(this.text());
    }, delay);
  }

  /** Empties the box and publishes the empty term at once. */
  clear(): void {
    this.cancelPending();
    this.text.set('');
    this.term.set('');
  }

  private cancelPending(): void {
    if (this.timer !== null) {
      clearTimeout(this.timer);
      this.timer = null;
    }
  }
}
