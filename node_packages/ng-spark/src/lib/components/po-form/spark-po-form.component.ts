import { ChangeDetectionStrategy, Component, computed, ContentChildren, inject, input, model, output, QueryList, signal, effect, TemplateRef } from '@angular/core';
import { CommonModule, NgTemplateOutlet } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsGridComponent, BsGridRowDirective, BsGridColumnDirective, BsGridColDirective, BsColFormLabelDirective } from '@mintplayer/ng-bootstrap/grid';
import { BsInputGroupComponent } from '@mintplayer/ng-bootstrap/input-group';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { BsSelectComponent, BsSelectOption } from '@mintplayer/ng-bootstrap/select';
import { BsModalHostComponent, BsModalDirective, BsModalHeaderDirective, BsModalBodyDirective, BsModalFooterDirective } from '@mintplayer/ng-bootstrap/modal';
import { BsDatatableComponent, BsDatatableColumnDirective, BsRowTemplateDirective, DatatableSettings } from '@mintplayer/ng-bootstrap/datatable';
import { BsToggleButtonComponent } from '@mintplayer/ng-bootstrap/toggle-button';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { BsTabControlComponent, BsTabPageComponent, BsTabPageHeaderDirective } from '@mintplayer/ng-bootstrap/tab-control';
import { PaginationResponse } from '@mintplayer/pagination';
import { SparkService } from '../../services/spark.service';
import { SparkLanguageService } from '../../services/spark-language.service';
import { TranslateKeyPipe } from '../../pipes/translate-key.pipe';
import { ResolveTranslationPipe } from '../../pipes/resolve-translation.pipe';
import { InputTypePipe } from '../../pipes/input-type.pipe';
import { LookupDisplayValuePipe } from '../../pipes/lookup-display-value.pipe';
import { LookupDisplayTypePipe } from '../../pipes/lookup-display-type.pipe';
import { LookupOptionsPipe } from '../../pipes/lookup-options.pipe';
import { ReferenceDisplayValuePipe } from '../../pipes/reference-display-value.pipe';
import { AsDetailDisplayValuePipe } from '../../pipes/as-detail-display-value.pipe';
import { AsDetailTypePipe } from '../../pipes/as-detail-type.pipe';
import { AsDetailColumnsPipe } from '../../pipes/as-detail-columns.pipe';
import { AsDetailCellValuePipe } from '../../pipes/as-detail-cell-value.pipe';
import { CanCreateDetailRowPipe } from '../../pipes/can-create-detail-row.pipe';
import { CanDeleteDetailRowPipe } from '../../pipes/can-delete-detail-row.pipe';
import { InlineRefOptionsPipe } from '../../pipes/inline-ref-options.pipe';
import { ReferenceAttrValuePipe } from '../../pipes/reference-attr-value.pipe';
import { ErrorForAttributePipe } from '../../pipes/error-for-attribute.pipe';
import { ELookupDisplayType, EntityPermissions, EntityType, EntityAttributeDefinition, LookupReference, LookupReferenceValue, PersistentObject, PersistentObjectAttribute, ValidationError, resolveTranslation } from '../../models';
import { AttributeTab, AttributeGroup } from '../../models/entity-type';
import { ShowedOn, hasShowedOnFlag } from '../../models/showed-on';
import { SparkIconComponent } from '../icon/spark-icon.component';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { SparkFieldTemplateDirective, SparkFieldTemplateContext } from '../../directives/spark-field-template.directive';

@Component({
  selector: 'spark-po-form',
  imports: [CommonModule, NgTemplateOutlet, FormsModule, BsCardComponent, BsCardHeaderComponent, BsFormComponent, BsFormControlDirective, BsGridComponent, BsGridRowDirective, BsGridColumnDirective, BsGridColDirective, BsColFormLabelDirective, BsButtonTypeDirective, BsInputGroupComponent, BsSelectComponent, BsSelectOption, BsModalHostComponent, BsModalDirective, BsModalHeaderDirective, BsModalBodyDirective, BsModalFooterDirective, BsDatatableComponent, BsDatatableColumnDirective, BsRowTemplateDirective, BsTableComponent, BsToggleButtonComponent, BsSpinnerComponent, BsTabControlComponent, BsTabPageComponent, BsTabPageHeaderDirective, SparkIconComponent, SparkPoFormComponent, TranslateKeyPipe, ResolveTranslationPipe, InputTypePipe, LookupDisplayValuePipe, LookupDisplayTypePipe, LookupOptionsPipe, ReferenceDisplayValuePipe, AsDetailDisplayValuePipe, AsDetailTypePipe, AsDetailColumnsPipe, AsDetailCellValuePipe, CanCreateDetailRowPipe, CanDeleteDetailRowPipe, InlineRefOptionsPipe, ReferenceAttrValuePipe, ErrorForAttributePipe],
  templateUrl: './spark-po-form.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class SparkPoFormComponent {
  private readonly sparkService = inject(SparkService);
  private readonly translations = inject(SparkLanguageService);

  @ContentChildren(SparkFieldTemplateDirective) fieldTemplates!: QueryList<SparkFieldTemplateDirective>;

  entityType = input<EntityType | null>(null);
  formData = model<Record<string, any>>({});
  validationErrors = input<ValidationError[]>([]);
  showButtons = input(false);
  isSaving = input(false);
  externalFieldTemplates = input<SparkFieldTemplateDirective[]>([]);

  save = output<void>();
  cancel = output<void>();

  colors = Color;
  referenceOptions = signal<Record<string, PersistentObject[]>>({});
  asDetailTypes = signal<Record<string, EntityType>>({});
  lookupReferenceOptions = signal<Record<string, LookupReference>>({});

  // Modal state for AsDetail object editing
  editingAsDetailAttr = signal<EntityAttributeDefinition | null>(null);
  asDetailFormData = signal<Record<string, any>>({});
  showAsDetailModal = signal(false);
  editingArrayIndex = signal<number | null>(null);

  // Permissions for array AsDetail entity types
  asDetailPermissions = signal<Record<string, EntityPermissions>>({});

  // Reference options for columns within array AsDetail types (keyed by parent attr name, then column name)
  asDetailReferenceOptions = signal<Record<string, Record<string, PersistentObject[]>>>({});

  // Modal state for Reference selection
  editingReferenceAttr = signal<EntityAttributeDefinition | null>(null);
  showReferenceModal = signal(false);
  referenceModalItems = signal<PersistentObject[]>([]);
  referenceModalEntityType = signal<EntityType | null>(null);
  referenceModalPagination = signal<PaginationResponse<PersistentObject> | undefined>(undefined);
  referenceModalSettings: DatatableSettings = new DatatableSettings({
    perPage: { values: [10, 25, 50], selected: 10 },
    page: { values: [1], selected: 1 },
    sortProperty: '',
    sortDirection: 'ascending'
  });
  referenceSearchTerm = '';

  // Modal state for LookupReference selection (Modal display type)
  editingLookupAttr = signal<EntityAttributeDefinition | null>(null);
  showLookupModal = signal(false);
  lookupModalItems = signal<LookupReferenceValue[]>([]);
  lookupSearchTerm = signal('');
  ELookupDisplayType = ELookupDisplayType;

  editableAttributes = computed(() => {
    return this.entityType()?.attributes
      .filter(a => a.isVisible && !a.isReadOnly && hasShowedOnFlag(a.showedOn, ShowedOn.PersistentObject))
      .sort((a, b) => a.order - b.order) || [];
  });

  private static readonly DEFAULT_TAB: AttributeTab = { id: '__default__', name: 'Algemeen', label: { nl: 'Algemeen', en: 'General' }, order: 0 };

  ungroupedAttributes = computed(() => {
    const attrs = this.editableAttributes();
    const groupIds = new Set((this.entityType()?.groups || []).map(g => g.id));
    return attrs.filter(a => !a.group || !groupIds.has(a.group));
  });

  resolvedTabs = computed((): AttributeTab[] => {
    const et = this.entityType();
    const definedTabs = et?.tabs?.length ? [...et.tabs].sort((a, b) => a.order - b.order) : [];
    const hasUngroupedAttrs = this.ungroupedAttributes().length > 0;
    const hasUntabbedGroups = (et?.groups || []).some(g => !g.tab);

    if (hasUngroupedAttrs || hasUntabbedGroups || definedTabs.length === 0) {
      return [SparkPoFormComponent.DEFAULT_TAB, ...definedTabs];
    }
    return definedTabs;
  });

  groupsForTab(tab: AttributeTab): AttributeGroup[] {
    const groups = this.entityType()?.groups || [];
    if (tab.id === '__default__') {
      return groups.filter(g => !g.tab).sort((a, b) => a.order - b.order);
    }
    return groups.filter(g => g.tab === tab.id).sort((a, b) => a.order - b.order);
  }

  attrsForGroup(group: AttributeGroup): EntityAttributeDefinition[] {
    return this.editableAttributes().filter(a => a.group === group.id);
  }

  referenceVisibleAttributes = computed(() => {
    return this.referenceModalEntityType()?.attributes
      .filter(a => a.isVisible)
      .sort((a, b) => a.order - b.order) || [];
  });

  filteredLookupItems = computed(() => {
    if (!this.lookupSearchTerm().trim()) {
      return this.lookupModalItems();
    }
    const term = this.lookupSearchTerm().toLowerCase().trim();
    return this.lookupModalItems().filter(item => {
      const translation = resolveTranslation(item.values);
      return translation.toLowerCase().includes(term) || item.key.toLowerCase().includes(term);
    });
  });

  getFieldTemplate(attr: EntityAttributeDefinition): TemplateRef<SparkFieldTemplateContext> | null {
    const allTemplates = [
      ...(this.fieldTemplates?.toArray() || []),
      ...this.externalFieldTemplates()
    ];
    // Priority 1: match by field name
    const byName = allTemplates.find(t => t.name() === attr.name);
    if (byName) return byName.template;
    // Priority 2: match by data type
    const byType = allTemplates.find(t => t.name() === attr.dataType);
    if (byType) return byType.template;
    return null;
  }

  getFieldTemplateContext(attr: EntityAttributeDefinition): SparkFieldTemplateContext {
    const errorEntry = this.validationErrors().find(e => e.attributeName === attr.name);
    return {
      $implicit: attr,
      formData: this.formData(),
      value: this.formData()[attr.name],
      hasError: this.hasError(attr.name),
      errorMessage: errorEntry?.errorMessage || null
    };
  }

  constructor() {
    effect(() => {
      const et = this.entityType();
      if (et) {
        this.loadReferenceOptions();
        this.loadAsDetailTypes();
        this.loadLookupReferenceOptions();
      }
    });
  }

  private toRecord<T>(entries: [string, T][]): Record<string, T> {
    const result: Record<string, T> = {};
    for (const [key, value] of entries) {
      result[key] = value;
    }
    return result;
  }

  async loadReferenceOptions(): Promise<void> {
    const refAttrs = this.editableAttributes().filter(a => a.dataType === 'Reference' && a.query);
    if (refAttrs.length === 0) return;

    const entries = await Promise.all(
      refAttrs.filter(a => a.query).map(async attr => {
        const items = await this.sparkService.executeQueryByName(attr.query!);
        return [attr.name, items] as [string, PersistentObject[]];
      })
    );
    this.referenceOptions.set(this.toRecord(entries));
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

        if (attr.isArray) {
          const perms = await this.sparkService.getPermissions(asDetailType.id);
          this.asDetailPermissions.update(prev => ({ ...prev, [attr.name]: perms }));

          const refCols = asDetailType.attributes.filter(a => a.dataType === 'Reference' && a.query);
          if (refCols.length > 0) {
            const refEntries = await Promise.all(
              refCols.filter(c => c.query).map(async col => {
                const items = await this.sparkService.executeQueryByName(col.query!);
                return [col.name, items] as [string, PersistentObject[]];
              })
            );
            this.asDetailReferenceOptions.update(prev => ({ ...prev, [attr.name]: this.toRecord(refEntries) }));
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
        const ref = await this.sparkService.getLookupReference(name);
        return [name, ref] as [string, LookupReference];
      })
    );
    this.lookupReferenceOptions.set(this.toRecord(entries));
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
    this.editingLookupAttr.set(attr);
    this.lookupSearchTerm.set('');
    this.lookupModalItems.set(this.getLookupOptions(attr));
    this.showLookupModal.set(true);
  }

  selectLookupItem(item: LookupReferenceValue): void {
    const attr = this.editingLookupAttr();
    if (attr) {
      const data = { ...this.formData() };
      data[attr.name] = item.key;
      this.formData.set(data);
    }
    this.closeLookupModal();
  }

  closeLookupModal(): void {
    this.showLookupModal.set(false);
    this.editingLookupAttr.set(null);
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

  // AsDetail object modal methods
  openAsDetailEditor(attr: EntityAttributeDefinition): void {
    this.editingAsDetailAttr.set(attr);
    this.editingArrayIndex.set(null);
    this.asDetailFormData.set({ ...(this.formData()[attr.name] || {}) });
    this.showAsDetailModal.set(true);
  }

  saveAsDetailObject(): void {
    const attr = this.editingAsDetailAttr();
    if (attr) {
      const data = { ...this.formData() };
      if (attr.isArray) {
        const arr = [...(data[attr.name] || [])];
        const idx = this.editingArrayIndex();
        if (idx !== null) {
          arr[idx] = { ...this.asDetailFormData() };
        } else {
          arr.push({ ...this.asDetailFormData() });
        }
        data[attr.name] = arr;
      } else {
        data[attr.name] = { ...this.asDetailFormData() };
      }
      this.formData.set(data);
    }
    this.closeAsDetailModal();
  }

  closeAsDetailModal(): void {
    this.showAsDetailModal.set(false);
    this.editingAsDetailAttr.set(null);
    this.editingArrayIndex.set(null);
    this.asDetailFormData.set({});
  }

  // Inline AsDetail methods
  addInlineRow(attr: EntityAttributeDefinition): void {
    const data = { ...this.formData() };
    const arr = [...(data[attr.name] || [])];
    arr.push({});
    data[attr.name] = arr;
    this.formData.set(data);
  }

  // Array AsDetail methods
  addArrayItem(attr: EntityAttributeDefinition): void {
    this.editingAsDetailAttr.set(attr);
    this.editingArrayIndex.set(null);
    this.asDetailFormData.set({});
    this.showAsDetailModal.set(true);
  }

  editArrayItem(attr: EntityAttributeDefinition, index: number): void {
    this.editingAsDetailAttr.set(attr);
    this.editingArrayIndex.set(index);
    const arr = this.formData()[attr.name] || [];
    this.asDetailFormData.set({ ...(arr[index] || {}) });
    this.showAsDetailModal.set(true);
  }

  removeArrayItem(attr: EntityAttributeDefinition, index: number): void {
    const data = { ...this.formData() };
    const arr = [...(data[attr.name] || [])];
    arr.splice(index, 1);
    data[attr.name] = arr;
    this.formData.set(data);
  }

  // Reference modal methods
  async openReferenceSelector(attr: EntityAttributeDefinition): Promise<void> {
    this.editingReferenceAttr.set(attr);
    this.referenceSearchTerm = '';
    this.referenceModalItems.set(this.getReferenceOptions(attr));

    const types = await this.sparkService.getEntityTypes();
    this.referenceModalEntityType.set(types.find(t => t.clrType === attr.referenceType) || null);
    this.referenceModalSettings = new DatatableSettings({
      perPage: { values: [10, 25, 50], selected: 10 },
      page: { values: [1], selected: 1 },
      sortProperty: '',
      sortDirection: 'ascending'
    });
    this.applyReferenceFilter();
    this.showReferenceModal.set(true);
  }

  onReferenceSearchChange(): void {
    this.referenceModalSettings.page.selected = 1;
    this.applyReferenceFilter();
  }

  applyReferenceFilter(): void {
    let filteredItems = this.referenceModalItems();

    if (this.referenceSearchTerm.trim()) {
      const term = this.referenceSearchTerm.toLowerCase().trim();
      filteredItems = this.referenceModalItems().filter(item => {
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
    this.referenceModalPagination.set({
      data: filteredItems,
      totalRecords: filteredItems.length,
      totalPages: totalPages,
      perPage: this.referenceModalSettings.perPage.selected,
      page: this.referenceModalSettings.page.selected
    });

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
    const attr = this.editingReferenceAttr();
    if (attr) {
      const data = { ...this.formData() };
      data[attr.name] = item.id;
      this.formData.set(data);
    }
    this.closeReferenceModal();
  }

  closeReferenceModal(): void {
    this.showReferenceModal.set(false);
    this.editingReferenceAttr.set(null);
    this.referenceModalItems.set([]);
    this.referenceModalEntityType.set(null);
    this.referenceSearchTerm = '';
  }
}
