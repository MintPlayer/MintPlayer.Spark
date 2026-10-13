import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsGridComponent, BsGridRowDirective, BsGridColumnDirective } from '@mintplayer/ng-bootstrap/grid';
import { BsInputGroupComponent } from '@mintplayer/ng-bootstrap/input-group';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { BsModalHostComponent, BsModalDirective, BsModalHeaderDirective, BsModalBodyDirective, BsModalFooterDirective } from '@mintplayer/ng-bootstrap/modal';
import { BsDatatableComponent, BsDatatableColumnDirective, BsRowTemplateDirective, DatatableSettings } from '@mintplayer/ng-bootstrap/datatable';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { PaginationResponse } from '@mintplayer/pagination';
import { SparkService, SparkLanguageService, sparkDatatableLabels } from '@mintplayer/ng-spark/services';
import { EntityType, QueryResultItem, ShowedOn, hasShowedOnFlag } from '@mintplayer/ng-spark/models';
import { TranslateKeyPipe, ResolveTranslationPipe, ReferenceAttrValuePipe, LoadedReference, referenceLabel } from '@mintplayer/ng-spark/pipes';
import { SparkAttributeDescriptionComponent } from '@mintplayer/ng-spark/attribute-description';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';

/**
 * Reusable Reference value picker: a readonly textbox showing the current selection's
 * breadcrumb/name plus a "…" button that opens a searchable modal grid over the candidate
 * `options`. Per-instance state (no single top-level slot), so it works for top-level
 * reference attributes and for individual inline AsDetail reference cells alike.
 *
 * The parent pre-loads `options` (the query result) and passes `referenceType` (the target
 * CLR type); the component lazily loads that EntityType's metadata on first open to render
 * the grid's column headers. Emits the picked id via `valueChange`.
 */
@Component({
  selector: 'spark-reference-picker',
  imports: [FormsModule, BsFormComponent, BsFormControlDirective, BsGridComponent, BsGridRowDirective, BsGridColumnDirective, BsInputGroupComponent, BsButtonTypeDirective, BsModalHostComponent, BsModalDirective, BsModalHeaderDirective, BsModalBodyDirective, BsModalFooterDirective, BsDatatableComponent, BsDatatableColumnDirective, BsRowTemplateDirective, BsSpinnerComponent, SparkIconComponent, TranslateKeyPipe, ResolveTranslationPipe, ReferenceAttrValuePipe, SparkAttributeDescriptionComponent],
  templateUrl: './spark-reference-picker.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class SparkReferencePickerComponent {
  private readonly sparkService = inject(SparkService);
  private readonly lang = inject(SparkLanguageService);

  /** Currently selected referenced id (or null when unset). */
  value = input<string | null>(null);
  /** Candidate items to pick from (the full query result, pre-loaded by the parent). */
  options = input<QueryResultItem[]>([]);
  /** Target entity type's CLR type name; used to load the grid's column headers. */
  referenceType = input<string | undefined>(undefined);
  /** Renders the field with the Bootstrap is-invalid state. */
  isInvalid = input(false);
  /** Optional id forwarded to the readonly input (for label association). */
  inputId = input<string | undefined>(undefined);
  /** Optional resolved label appended to the modal header ("Select {title}"). */
  title = input<string>('');
  /**
   * The id the attribute held when the object was loaded and its server-resolved label
   * (`PersistentObjectAttribute.breadcrumb`); unset on a create.
   */
  loaded = input<LoadedReference | null | undefined>(undefined);

  valueChange = output<string | null>();

  colors = Color;

  showModal = signal(false);
  entityType = signal<EntityType | null>(null);
  pagination = signal<PaginationResponse<QueryResultItem> | undefined>(undefined);
  settings = signal(new DatatableSettings({
    perPage: { values: [10, 25, 50], selected: 10 },
    page: { values: [1], selected: 1 },
    sortColumns: []
  }));
  searchTerm = '';

  /** The picker lists query rows, so its columns are the query's: `showedOn` includes Query (#264; not None). */
  visibleAttributes = computed(() => {
    return this.entityType()?.attributes
      .filter(a => hasShowedOnFlag(a.showedOn, ShowedOn.Query))
      .sort((a, b) => a.order - b.order) || [];
  });

  /** The resize handle and its options dialog, translated; columns are named by `bsDatatableColumnLabel`. */
  datatableLabels = computed(() => sparkDatatableLabels(this.lang));

  // Typed rows so the datatable generic infers the row type.
  rows = computed<QueryResultItem[]>(() => this.pagination()?.data ?? []);

  /**
   * The textbox shows the server's label while the value is the loaded one, the chosen option row's
   * text after a change, and the id as a last resort (D10, see `referenceLabel`). The options are
   * only the query's first page, so they cannot label a stored value on their own.
   */
  displayValue = computed(() => {
    const id = this.value();
    if (!id) return this.lang.t('common.notSelected');
    return referenceLabel(id, this.loaded(), this.options());
  });

  async open(): Promise<void> {
    this.searchTerm = '';
    this.settings.set(new DatatableSettings({
      perPage: { values: [10, 25, 50], selected: 10 },
      page: { values: [1], selected: 1 },
      sortColumns: []
    }));

    // Lazily resolve the target entity type for the grid's column headers.
    const refType = this.referenceType();
    if (!this.entityType() && refType) {
      const types = await this.sparkService.getEntityTypes();
      this.entityType.set(types.find(t => t.clrType === refType) || null);
    }

    this.applyFilter();
    this.showModal.set(true);
  }

  onSearchChange(): void {
    this.settings().page.selected = 1;
    this.applyFilter();
  }

  applyFilter(): void {
    let filteredItems = this.options();

    if (this.searchTerm.trim()) {
      const term = this.searchTerm.toLowerCase().trim();
      filteredItems = this.options().filter(item => {
        if (item.breadcrumb?.toLowerCase().includes(term)) return true;
        return item.values.some(v => {
          const value = v.breadcrumb ?? v.value;
          if (value == null) return false;
          return String(value).toLowerCase().includes(term);
        });
      });
    }

    const totalPages = Math.ceil(filteredItems.length / this.settings().perPage.selected) || 1;
    this.pagination.set({
      data: filteredItems,
      totalRecords: filteredItems.length,
      totalPages: totalPages,
      perPage: this.settings().perPage.selected,
      page: this.settings().page.selected
    });

    this.settings().page.values = Array.from({ length: totalPages }, (_, i) => i + 1);

    if (this.settings().page.selected > totalPages) {
      this.settings().page.selected = 1;
    }
  }

  clearSearch(): void {
    this.searchTerm = '';
    this.onSearchChange();
  }

  select(item: QueryResultItem): void {
    this.valueChange.emit(item.id ?? null);
    this.close();
  }

  close(): void {
    this.showModal.set(false);
    this.searchTerm = '';
  }
}
