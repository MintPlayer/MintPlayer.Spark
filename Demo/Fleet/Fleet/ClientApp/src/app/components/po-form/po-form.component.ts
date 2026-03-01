import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, model, signal } from '@angular/core';
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
import { ELookupDisplayType, EntityPermissions, EntityType, EntityAttributeDefinition, LookupReference, LookupReferenceValue, PersistentObject, ValidationError } from '../../core/models';
import { ShowedOn, hasShowedOnFlag } from '../../core/models/showed-on';
import { IconComponent } from '../icon/icon.component';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
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

@Component({
  selector: 'app-po-form',
  imports: [CommonModule, FormsModule, BsFormComponent, BsFormControlDirective, BsGridComponent, BsGridRowDirective, BsGridColumnDirective, BsGridColDirective, BsColFormLabelDirective, BsButtonTypeDirective, BsInputGroupComponent, BsSelectComponent, BsSelectOption, BsModalHostComponent, BsModalDirective, BsModalHeaderDirective, BsModalBodyDirective, BsModalFooterDirective, BsDatatableComponent, BsDatatableColumnDirective, BsRowTemplateDirective, BsTableComponent, BsToggleButtonComponent, IconComponent, PoFormComponent, TranslatePipe, TranslateKeyPipe, InputTypePipe, LookupDisplayValuePipe, LookupDisplayTypePipe, LookupOptionsPipe, ReferenceDisplayValuePipe, AsDetailDisplayValuePipe, AsDetailTypePipe, AsDetailColumnsPipe, AsDetailCellValuePipe, CanCreateDetailRowPipe, CanDeleteDetailRowPipe, InlineRefOptionsPipe, ReferenceAttrValuePipe, ErrorForAttributePipe],
  templateUrl: './po-form.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class PoFormComponent {
  private readonly sparkService = inject(SparkService);

  entityType = input<EntityType | null>(null);
  formData = model<Record<string, any>>({});
  validationErrors = input<ValidationError[]>([]);
  showButtons = input(false);
  isSaving = input(false);

  save = output<void>();
  cancel = output<void>();

  private readonly lang = inject(LanguageService);
  colors = Color;
  referenceOptions = signal<Record<string, PersistentObject[]>>({});
  asDetailTypes = signal<Record<string, EntityType>>({});
  lookupReferenceOptions = signal<Record<string, LookupReference>>({});

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
  referenceModalEntityType = signal<EntityType | null>(null);
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
  lookupSearchTerm = signal<string>('');
  ELookupDisplayType = ELookupDisplayType;

  editableAttributes = computed(() => {
    const et = this.entityType();
    return et?.attributes
      .filter(a => a.isVisible && !a.isReadOnly && hasShowedOnFlag(a.showedOn, ShowedOn.PersistentObject))
      .sort((a, b) => a.order - b.order) || [];
  });

  referenceVisibleAttributes = computed(() => {
    return this.referenceModalEntityType()?.attributes
      .filter(a => a.isVisible)
      .sort((a, b) => a.order - b.order) || [];
  });

  filteredLookupItems = computed(() => {
    const term = this.lookupSearchTerm().toLowerCase().trim();
    const items = this.lookupModalItems();
    if (!term) {
      return items;
    }
    return items.filter(item => {
      const translation = this.lang.resolve(item.values);
      return translation.toLowerCase().includes(term) || item.key.toLowerCase().includes(term);
    });
  });

  constructor() {
    // Side effect: reload reference/lookup/asDetail data when entityType changes
    effect(() => {
      const et = this.entityType();
      if (et) {
        this.loadReferenceOptions();
        this.loadAsDetailTypes();
        this.loadLookupReferenceOptions();
      }
    });
  }

  async loadReferenceOptions(): Promise<void> {
    const et = this.entityType();
    const refAttrs = et?.attributes
      .filter(a => a.isVisible && !a.isReadOnly && hasShowedOnFlag(a.showedOn, ShowedOn.PersistentObject))
      .filter(a => a.dataType === 'Reference' && a.query) || [];

    if (refAttrs.length === 0) return;

    const entries = await Promise.all(
      refAttrs.map(async attr => {
        const results = await this.sparkService.executeQueryByName(attr.query!);
        return [attr.name, results] as const;
      })
    );
    this.referenceOptions.set(Object.fromEntries(entries));
  }

  async loadAsDetailTypes(): Promise<void> {
    const et = this.entityType();
    const asDetailAttrs = et?.attributes
      .filter(a => a.isVisible && !a.isReadOnly && hasShowedOnFlag(a.showedOn, ShowedOn.PersistentObject))
      .filter(a => a.dataType === 'AsDetail' && a.asDetailType) || [];

    if (asDetailAttrs.length === 0) return;

    const types = await this.sparkService.getEntityTypes();
    const newAsDetailTypes: Record<string, EntityType> = {};

    for (const attr of asDetailAttrs) {
      const asDetailType = types.find(t => t.clrType === attr.asDetailType);
      if (asDetailType) {
        newAsDetailTypes[attr.name] = asDetailType;

        if (attr.isArray) {
          const perms = await this.sparkService.getPermissions(asDetailType.id);
          this.asDetailPermissions.update(prev => ({ ...prev, [attr.name]: perms }));

          const refCols = asDetailType.attributes.filter(a => a.dataType === 'Reference' && a.query);
          if (refCols.length > 0) {
            const refEntries = await Promise.all(
              refCols.map(async col => {
                const results = await this.sparkService.executeQueryByName(col.query!);
                return [col.name, results] as const;
              })
            );
            this.asDetailReferenceOptions.update(prev => ({
              ...prev,
              [attr.name]: Object.fromEntries(refEntries)
            }));
          }
        }
      }
    }
    this.asDetailTypes.set(newAsDetailTypes);
  }

  async loadLookupReferenceOptions(): Promise<void> {
    const et = this.entityType();
    const lookupAttrs = et?.attributes
      .filter(a => a.isVisible && !a.isReadOnly && hasShowedOnFlag(a.showedOn, ShowedOn.PersistentObject))
      .filter(a => a.lookupReferenceType) || [];

    if (lookupAttrs.length === 0) return;

    const lookupNames = [...new Set(lookupAttrs.map(a => a.lookupReferenceType!))];
    const entries = await Promise.all(
      lookupNames.map(async name => {
        const result = await this.sparkService.getLookupReference(name);
        return [name, result] as const;
      })
    );
    this.lookupReferenceOptions.set(Object.fromEntries(entries));
  }

  getReferenceOptions(attr: EntityAttributeDefinition): PersistentObject[] {
    return this.referenceOptions()[attr.name] || [];
  }

  getLookupOptions(attr: EntityAttributeDefinition): LookupReferenceValue[] {
    const lookupRef = attr.lookupReferenceType ? this.lookupReferenceOptions()[attr.lookupReferenceType] : null;
    return lookupRef?.values.filter(v => v.isActive) || [];
  }

  // LookupReference modal methods
  openLookupSelector(attr: EntityAttributeDefinition): void {
    this.editingLookupAttr = attr;
    this.lookupSearchTerm.set('');
    this.lookupModalItems.set(this.getLookupOptions(attr));
    this.showLookupModal = true;
  }

  selectLookupItem(item: LookupReferenceValue): void {
    if (this.editingLookupAttr) {
      const data = this.formData();
      data[this.editingLookupAttr.name] = item.key;
      this.formData.set({ ...data });
    }
    this.closeLookupModal();
  }

  closeLookupModal(): void {
    this.showLookupModal = false;
    this.editingLookupAttr = null;
    this.lookupModalItems.set([]);
    this.lookupSearchTerm.set('');
  }

  hasError(attrName: string): boolean {
    return this.validationErrors().some(e => e.attributeName === attrName);
  }

  onFieldChange(): void {
    this.formData.set({ ...this.formData() });
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
    this.asDetailFormData = { ...(this.formData()[attr.name] || {}) };
    this.showAsDetailModal = true;
  }

  saveAsDetailObject(): void {
    if (this.editingAsDetailAttr) {
      const data = this.formData();
      if (this.editingAsDetailAttr.isArray) {
        const arr = [...(data[this.editingAsDetailAttr.name] || [])];
        if (this.editingArrayIndex !== null) {
          arr[this.editingArrayIndex] = { ...this.asDetailFormData };
        } else {
          arr.push({ ...this.asDetailFormData });
        }
        data[this.editingAsDetailAttr.name] = arr;
      } else {
        data[this.editingAsDetailAttr.name] = { ...this.asDetailFormData };
      }
      this.formData.set({ ...data });
    }
    this.closeAsDetailModal();
  }

  closeAsDetailModal(): void {
    this.showAsDetailModal = false;
    this.editingAsDetailAttr = null;
    this.editingArrayIndex = null;
    this.asDetailFormData = {};
  }

  // Inline AsDetail methods
  addInlineRow(attr: EntityAttributeDefinition): void {
    const data = this.formData();
    const arr = data[attr.name] || [];
    arr.push({});
    data[attr.name] = arr;
    this.formData.set({ ...data });
  }

  // Array AsDetail methods
  addArrayItem(attr: EntityAttributeDefinition): void {
    this.editingAsDetailAttr = attr;
    this.editingArrayIndex = null;
    this.asDetailFormData = {};
    this.showAsDetailModal = true;
  }

  editArrayItem(attr: EntityAttributeDefinition, index: number): void {
    this.editingAsDetailAttr = attr;
    this.editingArrayIndex = index;
    const arr = this.formData()[attr.name] || [];
    this.asDetailFormData = { ...(arr[index] || {}) };
    this.showAsDetailModal = true;
  }

  removeArrayItem(attr: EntityAttributeDefinition, index: number): void {
    const data = this.formData();
    const arr = [...(data[attr.name] || [])];
    arr.splice(index, 1);
    data[attr.name] = arr;
    this.formData.set({ ...data });
  }

  // Reference modal methods
  async openReferenceSelector(attr: EntityAttributeDefinition): Promise<void> {
    this.editingReferenceAttr = attr;
    this.referenceSearchTerm = '';
    this.referenceModalItems = this.getReferenceOptions(attr);

    const types = await this.sparkService.getEntityTypes();
    this.referenceModalEntityType.set(types.find(t => t.clrType === attr.referenceType) || null);
    this.referenceModalSettings = new DatatableSettings({
      perPage: { values: [10, 25, 50], selected: 10 },
      page: { values: [1], selected: 1 },
      sortProperty: '',
      sortDirection: 'ascending'
    });
    this.applyReferenceFilter();
    this.showReferenceModal = true;
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
  }

  clearReferenceSearch(): void {
    this.referenceSearchTerm = '';
    this.onReferenceSearchChange();
  }

  selectReferenceItem(item: PersistentObject): void {
    if (this.editingReferenceAttr) {
      const data = this.formData();
      data[this.editingReferenceAttr.name] = item.id;
      this.formData.set({ ...data });
    }
    this.closeReferenceModal();
  }

  closeReferenceModal(): void {
    this.showReferenceModal = false;
    this.editingReferenceAttr = null;
    this.referenceModalItems = [];
    this.referenceModalEntityType.set(null);
    this.referenceSearchTerm = '';
  }
}
