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
import { SparkService } from '../../core/services/spark.service';
import { EntityType, EntityAttributeDefinition, PersistentObject, PersistentObjectAttribute, ValidationError } from '../../core/models';
import { forkJoin } from 'rxjs';

@Component({
  selector: 'app-po-form',
  imports: [CommonModule, FormsModule, BsFormModule, BsGridModule, BsButtonTypeDirective, BsInputGroupComponent, BsSelectModule, BsModalModule],
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

  colors = Color;
  referenceOptions: Record<string, PersistentObject[]> = {};
  asDetailTypes: Record<string, EntityType> = {};

  // Modal state for AsDetail object editing
  editingAsDetailAttr: EntityAttributeDefinition | null = null;
  asDetailFormData: Record<string, any> = {};
  showAsDetailModal = false;

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['entityType'] && this.entityType) {
      this.loadReferenceOptions();
      this.loadAsDetailTypes();
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
        }
      });
      this.cdr.markForCheck();
    });
  }

  getEditableAttributes(): EntityAttributeDefinition[] {
    return this.entityType?.attributes
      .filter(a => a.isVisible && !a.isReadOnly)
      .sort((a, b) => a.order - b.order) || [];
  }

  getReferenceOptions(attr: EntityAttributeDefinition): PersistentObject[] {
    return this.referenceOptions[attr.name] || [];
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
    return error?.errorMessage || null;
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
    if (!value) return '(not set)';

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

    return '(click to edit)';
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
    // Copy current AsDetail data or initialize empty object
    this.asDetailFormData = { ...(this.formData[attr.name] || {}) };
    this.showAsDetailModal = true;
    this.cdr.markForCheck();
  }

  saveAsDetailObject(): void {
    if (this.editingAsDetailAttr) {
      this.formData[this.editingAsDetailAttr.name] = { ...this.asDetailFormData };
      this.formDataChange.emit(this.formData);
    }
    this.closeAsDetailModal();
  }

  closeAsDetailModal(): void {
    this.showAsDetailModal = false;
    this.editingAsDetailAttr = null;
    this.asDetailFormData = {};
    this.cdr.markForCheck();
  }

  getAsDetailAttributes(attr: EntityAttributeDefinition): EntityAttributeDefinition[] {
    const asDetailType = this.getAsDetailType(attr);
    return asDetailType?.attributes
      .filter(a => a.isVisible && !a.isReadOnly && a.name !== 'Id')
      .sort((a, b) => a.order - b.order) || [];
  }
}
