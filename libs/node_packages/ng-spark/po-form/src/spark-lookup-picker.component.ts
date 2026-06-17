import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsGridComponent, BsGridRowDirective, BsGridColDirective } from '@mintplayer/ng-bootstrap/grid';
import { BsInputGroupComponent } from '@mintplayer/ng-bootstrap/input-group';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { BsModalHostComponent, BsModalDirective, BsModalHeaderDirective, BsModalBodyDirective, BsModalFooterDirective } from '@mintplayer/ng-bootstrap/modal';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { LookupReferenceValue, resolveTranslation } from '@mintplayer/ng-spark/models';
import { TranslateKeyPipe, ResolveTranslationPipe } from '@mintplayer/ng-spark/pipes';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';

/**
 * Reusable LookupReference value picker: a readonly textbox showing the current selection's
 * translated label plus a "…" button that opens a searchable modal list over the active
 * `options`. Per-instance state, so it serves both top-level lookup attributes (display type
 * Modal) and individual inline AsDetail lookup cells. Emits the picked key via `valueChange`.
 */
@Component({
  selector: 'spark-lookup-picker',
  imports: [FormsModule, BsFormComponent, BsFormControlDirective, BsGridComponent, BsGridRowDirective, BsGridColDirective, BsInputGroupComponent, BsButtonTypeDirective, BsModalHostComponent, BsModalDirective, BsModalHeaderDirective, BsModalBodyDirective, BsModalFooterDirective, BsTableComponent, SparkIconComponent, TranslateKeyPipe, ResolveTranslationPipe],
  templateUrl: './spark-lookup-picker.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class SparkLookupPickerComponent {
  /** Currently selected lookup key (or null when unset). */
  value = input<string | null>(null);
  /** Active lookup values to pick from. */
  options = input<LookupReferenceValue[]>([]);
  /** Renders the field with the Bootstrap is-invalid state. */
  isInvalid = input(false);
  /** Optional id forwarded to the readonly input (for label association). */
  inputId = input<string | undefined>(undefined);
  /** Optional resolved label appended to the modal header ("Select {title}"). */
  title = input<string>('');

  valueChange = output<string | null>();

  colors = Color;

  showModal = signal(false);
  searchTerm = signal('');

  displayValue = computed(() => {
    const key = this.value();
    if (key == null || key === '') return '';
    const selected = this.options().find(o => o.key === String(key));
    if (!selected) return String(key);
    return resolveTranslation(selected.values) || selected.key;
  });

  filteredItems = computed(() => {
    if (!this.searchTerm().trim()) {
      return this.options();
    }
    const term = this.searchTerm().toLowerCase().trim();
    return this.options().filter(item => {
      const translation = resolveTranslation(item.values);
      return translation.toLowerCase().includes(term) || item.key.toLowerCase().includes(term);
    });
  });

  open(): void {
    this.searchTerm.set('');
    this.showModal.set(true);
  }

  select(item: LookupReferenceValue): void {
    this.valueChange.emit(item.key);
    this.close();
  }

  close(): void {
    this.showModal.set(false);
    this.searchTerm.set('');
  }
}
