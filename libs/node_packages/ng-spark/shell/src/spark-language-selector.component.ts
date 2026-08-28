import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { KeyValuePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { BsSelectComponent, BsSelectOption } from '@mintplayer/ng-bootstrap/select';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { ResolveTranslationPipe } from '@mintplayer/ng-spark/pipes';

/**
 * The culture switcher: a `bs-select` over `SparkLanguageService`'s languages, persisting the
 * choice. Renders nothing when the app declares one language (or none), so hosts can include it
 * unconditionally — `<spark-shell>`'s topbar does exactly that as its trailing default.
 */
@Component({
  selector: 'spark-language-selector',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [KeyValuePipe, FormsModule, BsSelectComponent, BsSelectOption, ResolveTranslationPipe],
  template: `
    @if (lang.languages() | keyvalue; as langs) {
      @if (langs.length > 1) {
        <bs-select [ngModel]="lang.language()" (ngModelChange)="lang.setLanguage($event)">
          @for (l of langs; track l.key) {
            <option [ngValue]="l.key">{{ l.value | resolveTranslation }}</option>
          }
        </bs-select>
      }
    }
  `,
})
export class SparkLanguageSelectorComponent {
  protected readonly lang = inject(SparkLanguageService);
}
