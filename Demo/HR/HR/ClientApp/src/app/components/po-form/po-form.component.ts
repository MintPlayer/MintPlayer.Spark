import { ChangeDetectionStrategy, Component, EventEmitter, Input, OnChanges, Output, SimpleChanges, inject, ChangeDetectorRef } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsFormModule } from '@mintplayer/ng-bootstrap/form';
import { BsGridModule } from '@mintplayer/ng-bootstrap/grid';
import { BsInputGroupComponent } from '@mintplayer/ng-bootstrap/input-group';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { BsSelectModule } from '@mintplayer/ng-bootstrap/select';
import { BsModalModule } from '@mintplayer/ng-bootstrap/modal';
import { BsDatatableModule, DatatableSettings } from '@mintplayer/ng-bootstrap/datatable';
import { BsToggleButtonModule } from '@mintplayer/ng-bootstrap/toggle-button';
import { PaginationResponse } from '@mintplayer/pagination';
import { SparkService } from '../../core/services/spark.service';
import { ELookupDisplayType, EntityPermissions, EntityType, EntityAttributeDefinition, LookupReference, LookupReferenceValue, PersistentObject, PersistentObjectAttribute, ValidationError, resolveTranslation } from '../../core/models';
import { ShowedOn, hasShowedOnFlag } from '../../core/models/showed-on';
import { LanguageService } from '../../core/services/language.service';
import { TranslatePipe } from '../../core/pipes/translate.pipe';
import { TranslateKeyPipe } from '../../core/pipes/translate-key.pipe';
import { IconComponent } from '../icon/icon.component';
import { forkJoin } from 'rxjs';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';

@Component({
  selector: 'app-po-form',
  imports: [CommonModule, FormsModule, BsFormModule, BsGridModule, BsButtonTypeDirective, BsInputGroupComponent, BsSelectModule, BsModalModule, BsDatatableModule, BsTableComponent, BsToggleButtonModule, IconComponent, PoFormComponent, TranslatePipe, TranslateKeyPipe],
  templateUrl: './po-form.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class PoFormComponent implements OnChanges {
  private readonly sparkService = inject(SparkService);
  private readonly cdr = inject(ChangeDetectorRef);

  @Input() entityType: EntityType | null = null;
  @Input() formData: Record<string, any> = {};
  @Input() validationErrors: ValidationError[] = [];
  @Input() showButtons = false;
  @Input() isSaving = false;

  @Output() formDataChange = new EventEmitter<Record<string, any>>();
  @Output() save = new EventEmitter<void>();
  @Output() cancel = new EventEmitter<void>();

  private readonly lang = inject(LanguageService);
  colors = Color;
  referenceOptions: Record<string, PersistentObject[]> = {};
  asDetailTypes: Record<string, EntityType> = {};
  lookupReferenceOptions: Record<string, LookupReference> = {};

  // Modal state for AsDetail object editing
  editingAsDetailAttr: EntityAttributeDefinition | null = null;
  asDetailFormData: Record<string, any> = {};
  showAsDetailModal = false;
  editingArrayIndex: number | null = null;

  // Permissions for array AsDetail entity types
  asDetailPermissions: Record<string, EntityPermissions> = {};

  // Reference options for columns within array AsDetail types (keyed by parent attr name, then column name)
  asDetailReferenceOptions: Record<string, Record<string, PersistentObject[]>> = {};

  // Modal state for Reference selection
  editingReferenceAttr: EntityAttributeDefinition | null = null;
  showReferenceModal = false;
  referenceModalItems: PersistentObject[] = [];
  referenceModalEntityType: EntityType | null = null;
  referenceModalPagination: PaginationResponse<PersistentObject> | undefined = undefined;
  referenceModalSettings: DatatableSettings = new DatatableSettings({
    perPage: { values: [10, 25, 50], selected: 10 },
    page: { values: [1], selected: 1 },
    sortProperty: '',
    sortDirection: 'ascending'
  });
  referenceSearchTerm: string = '';

  // Modal state for LookupReference selection (Modal display type)
  editingLookupAttr: EntityAttributeDefinition | null = null;
  showLookupModal = false;
  lookupModalItems: LookupReferenceValue[] = [];
  lookupSearchTerm: string = '';
  ELookupDisplayType = ELookupDisplayType;

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['entityType'] && this.entityType) {
      this.loadReferenceOptions();
      this.loadAsDetailTypes();
      this.loadLookupReferenceOptions();
    }
  }

  loadReferenceOptions(): void {
    const refAttrs = this.getEditableAttributes().filter(a => a.dataType === 'Reference' && a.query);

    if (refAttrs.length === 0) return;

    const queries: Record<string, ReturnType<typeof this.sparkService.executeQueryByName>> = {};
    refAttrs.forEach(attr => {
      if (attr.query) {
        queries[attr.name] = this.sparkService.executeQueryByName(attr.query);
      }
    });

    forkJoin(queries).subscribe(results => {
      this.referenceOptions = results;
      this.cdr.markForCheck();
    });
  }

  loadAsDetailTypes(): void {
    const asDetailAttrs = this.getEditableAttributes().filter(a => a.dataType === 'AsDetail' && a.asDetailType);

    if (asDetailAttrs.length === 0) return;

    this.sparkService.getEntityTypes().subscribe(types => {
      asDetailAttrs.forEach(attr => {
        const asDetailType = types.find(t => t.clrType === attr.asDetailType);
        if (asDetailType) {
          this.asDetailTypes[attr.name] = asDetailType;

          // Fetch permissions and reference options for array AsDetail entity types
          if (attr.isArray) {
            this.sparkService.getPermissions(asDetailType.id).subscribe(p => {
              this.asDetailPermissions[attr.name] = p;
              this.cdr.markForCheck();
            });

            // Load reference options for Reference columns within this AsDetail type
            const refCols = asDetailType.attributes.filter(a => a.dataType === 'Reference' && a.query);
            if (refCols.length > 0) {
              const refQueries: Record<string, ReturnType<typeof this.sparkService.executeQueryByName>> = {};
              refCols.forEach(col => {
                if (col.query) {
                  refQueries[col.name] = this.sparkService.executeQueryByName(col.query);
                }
              });
              forkJoin(refQueries).subscribe(results => {
                this.asDetailReferenceOptions[attr.name] = results;
                this.cdr.markForCheck();
              });
            }
          }
        }
      });
      this.cdr.markForCheck();
    });
  }

  loadLookupReferenceOptions(): void {
    const lookupAttrs = this.getEditableAttributes().filter(a => a.lookupReferenceType);

    if (lookupAttrs.length === 0) return;

    const lookupNames = [...new Set(lookupAttrs.map(a => a.lookupReferenceType!))];
    const queries: Record<string, ReturnType<typeof this.sparkService.getLookupReference>> = {};

    lookupNames.forEach(name => {
      queries[name] = this.sparkService.getLookupReference(name);
    });

    forkJoin(queries).subscribe(results => {
      this.lookupReferenceOptions = results;
      this.cdr.markForCheck();
    });
  }

  getEditableAttributes(): EntityAttributeDefinition[] {
    return this.entityType?.attributes
      .filter(a => a.isVisible && !a.isReadOnly && hasShowedOnFlag(a.showedOn, ShowedOn.PersistentObject))
      .sort((a, b) => a.order - b.order) || [];
  }

  getReferenceOptions(attr: EntityAttributeDefinition): PersistentObject[] {
    return this.referenceOptions[attr.name] || [];
  }

  getLookupOptions(attr: EntityAttributeDefinition): LookupReferenceValue[] {
    const lookupRef = attr.lookupReferenceType ? this.lookupReferenceOptions[attr.lookupReferenceType] : null;
    return lookupRef?.values.filter(v => v.isActive) || [];
  }

  getLookupDisplayValue(attr: EntityAttributeDefinition): string {
    const currentValue = this.formData[attr.name];
    if (currentValue == null || currentValue === '') return '';

    const options = this.getLookupOptions(attr);
    const selected = options.find(o => o.key === String(currentValue));
    if (!selected) return String(currentValue);

    return this.lang.resolve(selected.values) || selected.key;
  }

  getLookupDisplayType(attr: EntityAttributeDefinition): ELookupDisplayType {
    const lookupRef = attr.lookupReferenceType ? this.lookupReferenceOptions[attr.lookupReferenceType] : null;
    return lookupRef?.displayType ?? ELookupDisplayType.Dropdown;
  }

  // LookupReference modal methods
  openLookupSelector(attr: EntityAttributeDefinition): void {
    this.editingLookupAttr = attr;
    this.lookupSearchTerm = '';
    this.lookupModalItems = this.getLookupOptions(attr);
    this.showLookupModal = true;
    this.cdr.markForCheck();
  }

  getFilteredLookupItems(): LookupReferenceValue[] {
    if (!this.lookupSearchTerm.trim()) {
      return this.lookupModalItems;
    }
    const term = this.lookupSearchTerm.toLowerCase().trim();
    return this.lookupModalItems.filter(item => {
      const translation = this.lang.resolve(item.values);
      return translation.toLowerCase().includes(term) || item.key.toLowerCase().includes(term);
    });
  }

  selectLookupItem(item: LookupReferenceValue): void {
    if (this.editingLookupAttr) {
      this.formData[this.editingLookupAttr.name] = item.key;
      this.formDataChange.emit(this.formData);
    }
    this.closeLookupModal();
  }

  closeLookupModal(): void {
    this.showLookupModal = false;
    this.editingLookupAttr = null;
    this.lookupModalItems = [];
    this.lookupSearchTerm = '';
    this.cdr.markForCheck();
  }

  getAsDetailType(attr: EntityAttributeDefinition): EntityType | null {
    return this.asDetailTypes[attr.name] || null;
  }

  getInputType(dataType: string): string {
    switch (dataType) {
      case 'number':
      case 'decimal':
        return 'number';
      case 'boolean':
        return 'checkbox';
      case 'datetime':
        return 'datetime-local';
      case 'date':
        return 'date';
      default:
        return 'text';
    }
  }

  getErrorForAttribute(attrName: string): string | null {
    const error = this.validationErrors.find(e => e.attributeName === attrName);
    return error ? resolveTranslation(error.errorMessage) : null;
  }

  hasError(attrName: string): boolean {
    return this.validationErrors.some(e => e.attributeName === attrName);
  }

  onFieldChange(): void {
    this.formDataChange.emit(this.formData);
  }

  onSave(): void {
    this.save.emit();
  }

  onCancel(): void {
    this.cancel.emit();
  }

  // AsDetail object modal methods
  getAsDetailDisplayValue(attr: EntityAttributeDefinition): string {
    const value = this.formData[attr.name];
    if (!value) return this.lang.t('notSet');

    const asDetailType = this.getAsDetailType(attr);

    // 1. Try displayFormat (template with {PropertyName} placeholders)
    if (asDetailType?.displayFormat) {
      const result = this.resolveDisplayFormat(asDetailType.displayFormat, value);
      if (result && result.trim()) return result;
    }

    // 2. Try displayAttribute (single property name)
    if (asDetailType?.displayAttribute && value[asDetailType.displayAttribute]) {
      return value[asDetailType.displayAttribute];
    }

    // 3. Fallback to common property names
    const displayProps = ['Name', 'Title', 'Street', 'name', 'title'];
    for (const prop of displayProps) {
      if (value[prop]) return value[prop];
    }

    return this.lang.t('clickToEdit');
  }

  /**
   * Resolves a display format template by substituting {PropertyName} placeholders with actual values.
   * @param format The format template string (e.g., "{Street}, {PostalCode} {City}")
   * @param data The data object containing the property values
   * @returns The resolved string with placeholders replaced by values
   */
  private resolveDisplayFormat(format: string, data: Record<string, any>): string {
    return format.replace(/\{(\w+)\}/g, (match, propertyName) => {
      const value = data[propertyName];
      return value != null ? String(value) : '';
    });
  }

  openAsDetailEditor(attr: EntityAttributeDefinition): void {
    this.editingAsDetailAttr = attr;
    this.editingArrayIndex = null;
    // Copy current AsDetail data or initialize empty object
    this.asDetailFormData = { ...(this.formData[attr.name] || {}) };
    this.showAsDetailModal = true;
    this.cdr.markForCheck();
  }

  saveAsDetailObject(): void {
    if (this.editingAsDetailAttr) {
      if (this.editingAsDetailAttr.isArray) {
        // Array AsDetail: add or update item in array
        const arr = [...(this.formData[this.editingAsDetailAttr.name] || [])];
        if (this.editingArrayIndex !== null) {
          arr[this.editingArrayIndex] = { ...this.asDetailFormData };
        } else {
          arr.push({ ...this.asDetailFormData });
        }
        this.formData[this.editingAsDetailAttr.name] = arr;
      } else {
        // Single object AsDetail
        this.formData[this.editingAsDetailAttr.name] = { ...this.asDetailFormData };
      }
      this.formDataChange.emit(this.formData);
    }
    this.closeAsDetailModal();
  }

  closeAsDetailModal(): void {
    this.showAsDetailModal = false;
    this.editingAsDetailAttr = null;
    this.editingArrayIndex = null;
    this.asDetailFormData = {};
    this.cdr.markForCheck();
  }

  // Inline AsDetail methods
  addInlineRow(attr: EntityAttributeDefinition): void {
    const arr = this.formData[attr.name] || [];
    arr.push({});
    this.formData[attr.name] = arr;
    this.formDataChange.emit(this.formData);
    this.cdr.markForCheck();
  }

  getInlineReferenceOptions(parentAttr: EntityAttributeDefinition, col: EntityAttributeDefinition): PersistentObject[] {
    return this.asDetailReferenceOptions[parentAttr.name]?.[col.name] || [];
  }

  // Array AsDetail methods
  addArrayItem(attr: EntityAttributeDefinition): void {
    this.editingAsDetailAttr = attr;
    this.editingArrayIndex = null;
    this.asDetailFormData = {};
    this.showAsDetailModal = true;
    this.cdr.markForCheck();
  }

  editArrayItem(attr: EntityAttributeDefinition, index: number): void {
    this.editingAsDetailAttr = attr;
    this.editingArrayIndex = index;
    const arr = this.formData[attr.name] || [];
    this.asDetailFormData = { ...(arr[index] || {}) };
    this.showAsDetailModal = true;
    this.cdr.markForCheck();
  }

  removeArrayItem(attr: EntityAttributeDefinition, index: number): void {
    const arr = [...(this.formData[attr.name] || [])];
    arr.splice(index, 1);
    this.formData[attr.name] = arr;
    this.formDataChange.emit(this.formData);
    this.cdr.markForCheck();
  }

  getAsDetailColumns(attr: EntityAttributeDefinition): EntityAttributeDefinition[] {
    const type = this.getAsDetailType(attr);
    if (!type) return [];
    return type.attributes
      .filter(a => a.isVisible)
      .sort((a, b) => a.order - b.order);
  }

  getAsDetailCellValue(parentAttr: EntityAttributeDefinition, row: Record<string, any>, col: EntityAttributeDefinition): string {
    const value = row[col.name];
    if (value == null) return '';

    // For Reference columns, resolve breadcrumb from AsDetail reference options
    if (col.dataType === 'Reference' && col.query) {
      const parentOptions = this.asDetailReferenceOptions[parentAttr.name];
      if (parentOptions) {
        const options = parentOptions[col.name];
        if (options) {
          const match = options.find(o => o.id === value);
          if (match) return match.breadcrumb || match.name || String(value);
        }
      }
    }

    return String(value);
  }

  canCreateDetailRow(attr: EntityAttributeDefinition): boolean {
    const perms = this.asDetailPermissions[attr.name];
    return perms ? perms.canCreate : true;
  }

  canDeleteDetailRow(attr: EntityAttributeDefinition): boolean {
    const perms = this.asDetailPermissions[attr.name];
    return perms ? perms.canDelete : true;
  }

  // Reference modal methods
  getReferenceDisplayValue(attr: EntityAttributeDefinition): string {
    const selectedId = this.formData[attr.name];
    if (!selectedId) return this.lang.t('notSelected');

    const options = this.getReferenceOptions(attr);
    const selected = options.find(o => o.id === selectedId);
    return selected?.breadcrumb || selected?.name || selectedId;
  }

  openReferenceSelector(attr: EntityAttributeDefinition): void {
    this.editingReferenceAttr = attr;
    this.referenceSearchTerm = '';
    this.referenceModalItems = this.getReferenceOptions(attr);

    // Find the entity type for the reference
    this.sparkService.getEntityTypes().subscribe(types => {
      this.referenceModalEntityType = types.find(t => t.clrType === attr.referenceType) || null;
      this.referenceModalSettings = new DatatableSettings({
        perPage: { values: [10, 25, 50], selected: 10 },
        page: { values: [1], selected: 1 },
        sortProperty: '',
        sortDirection: 'ascending'
      });
      this.applyReferenceFilter();
      this.showReferenceModal = true;
      this.cdr.markForCheck();
    });
  }

  onReferenceSearchChange(): void {
    this.referenceModalSettings.page.selected = 1;
    this.applyReferenceFilter();
  }

  applyReferenceFilter(): void {
    let filteredItems = this.referenceModalItems;

    // Apply search filter
    if (this.referenceSearchTerm.trim()) {
      const term = this.referenceSearchTerm.toLowerCase().trim();
      filteredItems = this.referenceModalItems.filter(item => {
        if (item.name?.toLowerCase().includes(term)) return true;
        if (item.breadcrumb?.toLowerCase().includes(term)) return true;
        return item.attributes.some(attr => {
          const value = attr.breadcrumb || attr.value;
          if (value == null) return false;
          return String(value).toLowerCase().includes(term);
        });
      });
    }

    const totalPages = Math.ceil(filteredItems.length / this.referenceModalSettings.perPage.selected) || 1;
    this.referenceModalPagination = {
      data: filteredItems,
      totalRecords: filteredItems.length,
      totalPages: totalPages,
      perPage: this.referenceModalSettings.perPage.selected,
      page: this.referenceModalSettings.page.selected
    };

    this.referenceModalSettings.page.values = Array.from({ length: totalPages }, (_, i) => i + 1);

    if (this.referenceModalSettings.page.selected > totalPages) {
      this.referenceModalSettings.page.selected = 1;
    }

    this.cdr.markForCheck();
  }

  clearReferenceSearch(): void {
    this.referenceSearchTerm = '';
    this.onReferenceSearchChange();
  }

  getReferenceVisibleAttributes(): EntityAttributeDefinition[] {
    return this.referenceModalEntityType?.attributes
      .filter(a => a.isVisible)
      .sort((a, b) => a.order - b.order) || [];
  }

  getReferenceAttributeValue(item: PersistentObject, attrName: string): any {
    const attr = item.attributes.find(a => a.name === attrName);
    if (!attr) return '';
    if (attr.breadcrumb) return attr.breadcrumb;
    return attr.value ?? '';
  }

  selectReferenceItem(item: PersistentObject): void {
    if (this.editingReferenceAttr) {
      this.formData[this.editingReferenceAttr.name] = item.id;
      this.formDataChange.emit(this.formData);
    }
    this.closeReferenceModal();
  }

  closeReferenceModal(): void {
    this.showReferenceModal = false;
    this.editingReferenceAttr = null;
    this.referenceModalItems = [];
    this.referenceModalEntityType = null;
    this.referenceSearchTerm = '';
    this.cdr.markForCheck();
  }
}
