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
  standalone: true,
  imports: [CommonModule, FormsModule, BsFormModule, BsGridModule, BsButtonTypeDirective, BsInputGroupComponent, BsSelectModule, BsModalModule],
  templateUrl: './po-form.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class PoFormComponent implements OnChanges {
  private sparkService = inject(SparkService);
  private cdr = inject(ChangeDetectorRef);

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
  embeddedTypes: Record<string, EntityType> = {};

  // Modal state for embedded object editing
  editingEmbeddedAttr: EntityAttributeDefinition | null = null;
  embeddedFormData: Record<string, any> = {};
  showEmbeddedModal = false;

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['entityType'] && this.entityType) {
      this.loadReferenceOptions();
      this.loadEmbeddedTypes();
    }
  }

  loadReferenceOptions(): void {
    const refAttrs = this.getEditableAttributes().filter(a => a.dataType === 'reference' && a.query);

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

  loadEmbeddedTypes(): void {
    const embeddedAttrs = this.getEditableAttributes().filter(a => a.dataType === 'embedded' && a.embeddedType);

    if (embeddedAttrs.length === 0) return;

    this.sparkService.getEntityTypes().subscribe(types => {
      embeddedAttrs.forEach(attr => {
        const embeddedType = types.find(t => t.clrType === attr.embeddedType);
        if (embeddedType) {
          this.embeddedTypes[attr.name] = embeddedType;
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

  getEmbeddedType(attr: EntityAttributeDefinition): EntityType | null {
    return this.embeddedTypes[attr.name] || null;
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

  // Embedded object modal methods
  getEmbeddedDisplayValue(attr: EntityAttributeDefinition): string {
    const value = this.formData[attr.name];
    if (!value) return '(not set)';

    const embeddedType = this.getEmbeddedType(attr);
    if (embeddedType?.displayAttribute && value[embeddedType.displayAttribute]) {
      return value[embeddedType.displayAttribute];
    }

    // Try to find a reasonable display value
    const displayProps = ['Name', 'Title', 'Street', 'name', 'title'];
    for (const prop of displayProps) {
      if (value[prop]) return value[prop];
    }

    return '(click to edit)';
  }

  openEmbeddedEditor(attr: EntityAttributeDefinition): void {
    this.editingEmbeddedAttr = attr;
    // Copy current embedded data or initialize empty object
    this.embeddedFormData = { ...(this.formData[attr.name] || {}) };
    this.showEmbeddedModal = true;
    this.cdr.markForCheck();
  }

  saveEmbeddedObject(): void {
    if (this.editingEmbeddedAttr) {
      this.formData[this.editingEmbeddedAttr.name] = { ...this.embeddedFormData };
      this.formDataChange.emit(this.formData);
    }
    this.closeEmbeddedModal();
  }

  closeEmbeddedModal(): void {
    this.showEmbeddedModal = false;
    this.editingEmbeddedAttr = null;
    this.embeddedFormData = {};
    this.cdr.markForCheck();
  }

  getEmbeddedAttributes(attr: EntityAttributeDefinition): EntityAttributeDefinition[] {
    const embeddedType = this.getEmbeddedType(attr);
    return embeddedType?.attributes
      .filter(a => a.isVisible && !a.isReadOnly && a.name !== 'Id')
      .sort((a, b) => a.order - b.order) || [];
  }
}
