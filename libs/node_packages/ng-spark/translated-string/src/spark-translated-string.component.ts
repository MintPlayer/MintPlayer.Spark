import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { isTranslatedString, resolveTranslation, type TranslatedStringEntry } from '@mintplayer/ng-spark/models';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { languageLabel } from './language-label';

/**
 * The read-only view of a `TranslatedString` attribute (commerce X17) on a detail page: the text in the
 * current language — through the one fallback chain, `resolveTranslation` — and, when there is more than
 * one entry, every entry below it with its language, kind and validity window.
 */
@Component({
  selector: 'spark-translated-string',
  imports: [TranslateKeyPipe],
  templateUrl: './spark-translated-string.component.html',
  styleUrl: './spark-translated-string.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkTranslatedStringComponent {
  /** The attribute value; anything that is not a TranslatedString renders as unset. */
  value = input<unknown>(null);
  /** Lists every entry under the resolved text when there are several. */
  showEntries = input(true);

  private readonly language = inject(SparkLanguageService);

  protected readonly entries = computed<TranslatedStringEntry[]>(() => {
    const value = this.value();
    return isTranslatedString(value) ? value.Entries : [];
  });

  protected readonly text = computed(() => {
    const value = this.value();
    return isTranslatedString(value) ? resolveTranslation(value, this.language.language()) : '';
  });

  protected readonly listed = computed(() => (this.showEntries() && this.entries().length > 1 ? this.entries() : []));

  protected languageName(tag: string): string {
    return languageLabel(tag, this.language.language(), this.language.languages());
  }
}
