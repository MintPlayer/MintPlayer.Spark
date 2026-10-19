import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsInputGroupComponent } from '@mintplayer/ng-bootstrap/input-group';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';
import { isTranslatedString, type TranslatedString, type TranslatedStringEntry } from '@mintplayer/ng-spark/models';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { canonicalLanguageTag, isPlainEntry, languageLabel } from './language-label';

/** One editable language: its tag and its plain text ('' when there is none yet). */
export interface SparkTranslatedStringRow {
  tag: string;
  label: string;
  text: string;
}

/**
 * The editor of a `TranslatedString` attribute (commerce X17): one input per language. The rows are
 * the app's languages (`culture.json`), then any other language the value already has, then the ones
 * the user adds — any RFC 5646 tag, checked with `Intl.getCanonicalLocales`.
 *
 * Only plain entries (an app text: no kind, no validity) are edited here. Names with a kind or a
 * validity window — a place's official, historic and exonym names — are listed read-only and kept
 * exactly as they are on every emit; they come from imports and the API, not from a form.
 *
 * Clearing a language's input sends that tag with an empty text, which the server takes as "remove
 * this language" (`EntityMapper`'s per-tag merge keeps the languages a save does not mention).
 * Every change emits a new value, never a mutation of the one passed in.
 */
@Component({
  selector: 'spark-translated-string-editor',
  imports: [BsFormControlDirective, BsInputGroupComponent, BsButtonTypeDirective, SparkIconComponent, TranslateKeyPipe],
  templateUrl: './spark-translated-string-editor.component.html',
  styleUrl: './spark-translated-string-editor.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkTranslatedStringEditorComponent {
  /** The value as held by the form. Anything that is not a TranslatedString (null, the form's '' default) is empty. */
  value = input<unknown>(null);
  /** Shows the texts without inputs. */
  readonly = input(false);
  /** Renders the inputs with the Bootstrap is-invalid state. */
  isInvalid = input(false);
  /** Forwarded to the first input, so the form's `<label for>` lands on it. */
  inputId = input<string | undefined>(undefined);

  valueChange = output<TranslatedString>();
  /** An input lost focus: the form's blur hook (rule display, `triggersRefresh` on blur). */
  fieldBlur = output<void>();

  protected readonly colors = Color;
  private readonly language = inject(SparkLanguageService);

  /** Languages added with "Add a language" that have no text yet. */
  private readonly addedTags = signal<string[]>([]);
  protected readonly newTag = signal('');
  protected readonly newTagInvalid = signal(false);

  protected readonly entries = computed<TranslatedStringEntry[]>(() => {
    const value = this.value();
    return isTranslatedString(value) ? value.Entries : [];
  });

  protected readonly rows = computed<SparkTranslatedStringRow[]>(() => {
    const entries = this.entries();
    const tags: string[] = [];
    const add = (tag: string) => {
      if (!tags.some(t => t.toLowerCase() === tag.toLowerCase())) tags.push(tag);
    };
    Object.keys(this.language.languages()).forEach(add);
    entries.filter(isPlainEntry).forEach(e => add(e.Tag));
    this.addedTags().forEach(add);

    const ui = this.language.language();
    const languages = this.language.languages();
    return tags.map(tag => ({
      tag,
      label: languageLabel(tag, ui, languages),
      text: entries.find(e => isPlainEntry(e) && e.Tag.toLowerCase() === tag.toLowerCase())?.Value ?? '',
    }));
  });

  /** Names with a kind or validity: shown, never edited here. */
  protected readonly names = computed(() => this.entries().filter(e => !isPlainEntry(e)));

  update(tag: string, text: string): void {
    const next = [...this.entries()];
    const index = next.findIndex(e => isPlainEntry(e) && e.Tag.toLowerCase() === tag.toLowerCase());
    if (index >= 0) next[index] = { ...next[index], Value: text };
    else next.push({ Tag: tag, Value: text });
    this.valueChange.emit({ Entries: next });
  }

  onInput(tag: string, event: Event): void {
    this.update(tag, (event.target as HTMLInputElement).value);
  }

  onNewTagInput(event: Event): void {
    this.newTag.set((event.target as HTMLInputElement).value);
    this.newTagInvalid.set(false);
  }

  /** Adds a row for the typed tag, in its canonical form; refuses a tag that is not well-formed. */
  addLanguage(): void {
    const tag = canonicalLanguageTag(this.newTag());
    if (!tag) {
      this.newTagInvalid.set(true);
      return;
    }
    this.addedTags.update(tags => [...tags, tag]);
    this.newTag.set('');
  }
}
