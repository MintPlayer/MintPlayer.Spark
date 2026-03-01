import { ChangeDetectionStrategy, Component, computed, inject, ChangeDetectorRef, input, output, model, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsGridComponent, BsGridRowDirective, BsGridColumnDirective, BsGridColDirective, BsColFormLabelDirective } from '@mintplayer/ng-bootstrap/grid';
import { BsInputGroupComponent } from '@mintplayer/ng-bootstrap/input-group';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { BsSelectComponent, BsSelectOption } from '@mintplayer/ng-bootstrap/select';
import { BsModalHostComponent, BsModalDirective, BsModalHeaderDirective, BsModalBodyDirective, BsModalFooterDirective } from '@mintplayer/ng-bootstrap/modal';
import { BsDatatableComponent, BsDatatableColumnDirective, BsRowTemplateDirective, DatatableSettings } from '@mintplayer/ng-bootstrap/datatable';
import { BsToggleButtonComponent } from '@mintplayer/ng-bootstrap/toggle-button';
import { PaginationResponse } from '@mintplayer/pagination';
import { SparkService } from '../../core/services/spark.service';
import { ELookupDisplayType, EntityPermissions, EntityType, EntityAttributeDefinition, LookupReference, LookupReferenceValue, PersistentObject, PersistentObjectAttribute, ValidationError } from '../../core/models';
import { ShowedOn, hasShowedOnFlag } from '../../core/models/showed-on';
import { LanguageService } from '../../core/services/language.service';
import { TranslatePipe } from '../../core/pipes/translate.pipe';
import { TranslateKeyPipe } from '../../core/pipes/translate-key.pipe';
import { InputTypePipe } from '../../core/pipes/input-type.pipe';
import { LookupDisplayValuePipe } from '../../core/pipes/lookup-display-value.pipe';
import { LookupDisplayTypePipe } from '../../core/pipes/lookup-display-type.pipe';
import { LookupOptionsPipe } from '../../core/pipes/lookup-options.pipe';
import { ReferenceDisplayValuePipe } from '../../core/pipes/reference-display-value.pipe';
import { AsDetailDisplayValuePipe } from '../../core/pipes/as-detail-display-value.pipe';
import { AsDetailTypePipe } from '../../core/pipes/as-detail-type.pipe';
import { AsDetailColumnsPipe } from '../../core/pipes/as-detail-columns.pipe';
import { AsDetailCellValuePipe } from '../../core/pipes/as-detail-cell-value.pipe';
import { CanCreateDetailRowPipe } from '../../core/pipes/can-create-detail-row.pipe';
import { CanDeleteDetailRowPipe } from '../../core/pipes/can-delete-detail-row.pipe';
import { InlineRefOptionsPipe } from '../../core/pipes/inline-ref-options.pipe';
import { ReferenceAttrValuePipe } from '../../core/pipes/reference-attr-value.pipe';
import { ErrorForAttributePipe } from '../../core/pipes/error-for-attribute.pipe';
import { IconComponent } from '../icon/icon.component';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';

@Component({
  selector: 'app-po-form',
  imports: [CommonModule, FormsModule, BsFormComponent, BsFormControlDirective, BsGridComponent, BsGridRowDirective, BsGridColumnDirective, BsGridColDirective, BsColFormLabelDirective, BsButtonTypeDirective, BsInputGroupComponent, BsSelectComponent, BsSelectOption, BsModalHostComponent, BsModalDirective, BsModalHeaderDirective, BsModalBodyDirective, BsModalFooterDirective, BsDatatableComponent, BsDatatableColumnDirective, BsRowTemplateDirective, BsTableComponent, BsToggleButtonComponent, IconComponent, PoFormComponent, TranslatePipe, TranslateKeyPipe, InputTypePipe, LookupDisplayValuePipe, LookupDisplayTypePipe, LookupOptionsPipe, ReferenceDisplayValuePipe, AsDetailDisplayValuePipe, AsDetailTypePipe, AsDetailColumnsPipe, AsDetailCellValuePipe, CanCreateDetailRowPipe, CanDeleteDetailRowPipe, InlineRefOptionsPipe, ReferenceAttrValuePipe, ErrorForAttributePipe],
  templateUrl: './po-form.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class PoFormComponent {
  private readonly sparkService = inject(SparkService);
  private readonly cdr = inject(ChangeDetectorRef);

  entityType = input<EntityType | null>(null);
  formData = model<Record<string, any>>({});
  validationErrors = input<ValidationError[]>([]);
  showButtons = input(false);
  isSaving = input(false);

  save = output<void>();
  cancel = output<void>();

  private readonly lang = inject(LanguageService);
  colors = Color;
  referenceOptions: Record<string, PersistentObject[]> = {};
  asDetailTypes = signal<Record<string, EntityType>>({});
  lookupReferenceOptions: Record<string, LookupReference> = {};

  // Modal state for AsDetail object editing
  editingAsDetailAttr: EntityAttributeDefinition | null = null;
  asDetailFormData: Record<string, any> = {};
  showAsDetailModal = false;
  editingArrayIndex: number | null = null;

  // Permissions for array AsDetail entity types
  asDetailPermissions = signal<Record<string, EntityPermissions>>({});

  // Reference options for columns within array AsDetail types (keyed by parent attr name, then column name)
  asDetailReferenceOptions = signal<Record<string, Record<string, PersistentObject[]>>>({});

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
  lookupModalItems = signal<LookupReferenceValue[]>([]);
  lookupSearchTerm = signal('');
  ELookupDisplayType = ELookupDisplayType;

  // Track previous entityType to detect changes
  private previousEntityTypeId: string | null = null;

  ngDoCheck(): void {
    const currentType = this.entityType();
    const currentId = currentType?.id ?? null;
    if (currentId !== this.previousEntityTypeId) {
      this.previousEntityTypeId = currentId;
      if (currentType) {
        this.loadReferenceOptions();
        this.loadAsDetailTypes();
        this.loadLookupReferenceOptions();
      }
    }
  }

  async loadReferenceOptions(): Promise<void> {
    const refAttrs = this.editableAttributes().filter(a => a.dataType === 'Reference' && a.query);

    if (refAttrs.length === 0) return;

    const entries = await Promise.all(
      refAttrs.filter(a => a.query).map(async attr => {
        const results = await this.sparkService.executeQueryByName(attr.query!);
        return [attr.name, results] as const;
      })
    );

    this.referenceOptions = Object.fromEntries(entries);
    this.cdr.markForCheck();
  }

  async loadAsDetailTypes(): Promise<void> {
    const asDetailAttrs = this.editableAttributes().filter(a => a.dataType === 'AsDetail' && a.asDetailType);

    if (asDetailAttrs.length === 0) return;

    const types = await this.sparkService.getEntityTypes();
    const newAsDetailTypes: Record<string, EntityType> = {};

    for (const attr of asDetailAttrs) {
      const asDetailType = types.find(t => t.clrType === attr.asDetailType);
      if (asDetailType) {
        newAsDetailTypes[attr.name] = asDetailType;

        // Fetch permissions and reference options for array AsDetail entity types
        if (attr.isArray) {
          const perms = await this.sparkService.getPermissions(asDetailType.id);
          this.asDetailPermissions.update(prev => ({ ...prev, [attr.name]: perms }));

          // Load reference options for Reference columns within this AsDetail type
          const refCols = asDetailType.attributes.filter(a => a.dataType === 'Reference' && a.query);
          if (refCols.length > 0) {
            const refEntries = await Promise.all(
              refCols.filter(c => c.query).map(async col => {
                const results = await this.sparkService.executeQueryByName(col.query!);
                return [col.name, results] as const;
              })
            );
            this.asDetailReferenceOptions.update(prev => ({ ...prev, [attr.name]: Object.fromEntries(refEntries) }));
          }
        }
      }
    }
    this.asDetailTypes.set(newAsDetailTypes);
  }

  async loadLookupReferenceOptions(): Promise<void> {
    const lookupAttrs = this.editableAttributes().filter(a => a.lookupReferenceType);

    if (lookupAttrs.length === 0) return;

    const lookupNames = [...new Set(lookupAttrs.map(a => a.lookupReferenceType!))];
    const entries = await Promise.all(
      lookupNames.map(async name => {
        const result = await this.sparkService.getLookupReference(name);
        return [name, result] as const;
      })
    );

    this.lookupReferenceOptions = Object.fromEntries(entries);
    this.cdr.markForCheck();
  }

  editableAttributes = computed(() => {
    return this.entityType()?.attributes
      .filter(a => a.isVisible && !a.isReadOnly && hasShowedOnFlag(a.showedOn, ShowedOn.PersistentObject))
      .sort((a, b) => a.order - b.order) || [];
  });

  getReferenceOptions(attr: EntityAttributeDefinition): PersistentObject[] {
    return this.referenceOptions[attr.name] || [];
  }

  getLookupOptions(attr: EntityAttributeDefinition): LookupReferenceValue[] {
    const lookupRef = attr.lookupReferenceType ? this.lookupReferenceOptions[attr.lookupReferenceType] : null;
    return lookupRef?.values.filter(v => v.isActive) || [];
  }

  // LookupReference modal methods
  openLookupSelector(attr: EntityAttributeDefinition): void {
    this.editingLookupAttr = attr;
    this.lookupSearchTerm.set('');
    this.lookupModalItems.set(this.getLookupOptions(attr));
    this.showLookupModal = true;
    this.cdr.markForCheck();
  }

  filteredLookupItems = computed(() => {
    const items = this.lookupModalItems();
    const term = this.lookupSearchTerm().trim();
    if (!term) {
      return items;
    }
    const lowerTerm = term.toLowerCase();
    return items.filter(item => {
      const translation = this.lang.resolve(item.values);
      return translation.toLowerCase().includes(lowerTerm) || item.key.toLowerCase().includes(lowerTerm);
    });
  });

  selectLookupItem(item: LookupReferenceValue): void {
    if (this.editingLookupAttr) {
      const data = this.formData();
      data[this.editingLookupAttr.name] = item.key;
      this.formData.set({...data});
    }
    this.closeLookupModal();
  }

  closeLookupModal(): void {
    this.showLookupModal = false;
    this.editingLookupAttr = null;
    this.lookupModalItems.set([]);
    this.lookupSearchTerm.set('');
    this.cdr.markForCheck();
  }

  hasError(attrName: string): boolean {
    return this.validationErrors().some(e => e.attributeName === attrName);
  }

  onFieldChange(): void {
    this.formData.set({...this.formData()});
  }

  onSave(): void {
    this.save.emit();
  }

  onCancel(): void {
    this.cancel.emit();
  }

  openAsDetailEditor(attr: EntityAttributeDefinition): void {
    this.editingAsDetailAttr = attr;
    this.editingArrayIndex = null;
    // Copy current AsDetail data or initialize empty object
    this.asDetailFormData = { ...(this.formData()[attr.name] || {}) };
    this.showAsDetailModal = true;
    this.cdr.markForCheck();
  }

  saveAsDetailObject(): void {
    if (this.editingAsDetailAttr) {
      const data = this.formData();
      if (this.editingAsDetailAttr.isArray) {
        // Array AsDetail: add or update item in array
        const arr = [...(data[this.editingAsDetailAttr.name] || [])];
        if (this.editingArrayIndex !== null) {
          arr[this.editingArrayIndex] = { ...this.asDetailFormData };
        } else {
          arr.push({ ...this.asDetailFormData });
        }
        data[this.editingAsDetailAttr.name] = arr;
      } else {
        // Single object AsDetail
        data[this.editingAsDetailAttr.name] = { ...this.asDetailFormData };
      }
      this.formData.set({...data});
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
    const data = this.formData();
    const arr = data[attr.name] || [];
    arr.push({});
    data[attr.name] = arr;
    this.formData.set({...data});
    this.cdr.markForCheck();
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
    const arr = this.formData()[attr.name] || [];
    this.asDetailFormData = { ...(arr[index] || {}) };
    this.showAsDetailModal = true;
    this.cdr.markForCheck();
  }

  removeArrayItem(attr: EntityAttributeDefinition, index: number): void {
    const data = this.formData();
    const arr = [...(data[attr.name] || [])];
    arr.splice(index, 1);
    data[attr.name] = arr;
    this.formData.set({...data});
    this.cdr.markForCheck();
  }

  async openReferenceSelector(attr: EntityAttributeDefinition): Promise<void> {
    this.editingReferenceAttr = attr;
    this.referenceSearchTerm = '';
    this.referenceModalItems = this.getReferenceOptions(attr);

    const types = await this.sparkService.getEntityTypes();
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

  get referenceVisibleAttributes(): EntityAttributeDefinition[] {
    return this.referenceModalEntityType?.attributes
      .filter(a => a.isVisible)
      .sort((a, b) => a.order - b.order) || [];
  }

  selectReferenceItem(item: PersistentObject): void {
    if (this.editingReferenceAttr) {
      const data = this.formData();
      data[this.editingReferenceAttr.name] = item.id;
      this.formData.set({...data});
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
