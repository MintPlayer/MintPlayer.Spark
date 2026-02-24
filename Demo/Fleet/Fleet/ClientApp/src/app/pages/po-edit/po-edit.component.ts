import { ChangeDetectorRef, Component, inject, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertModule } from '@mintplayer/ng-bootstrap/alert';
import { SparkService } from '../../core/services/spark.service';
import { EntityType, PersistentObject, PersistentObjectAttribute, ValidationError } from '../../core/models';
import { ShowedOn, hasShowedOnFlag } from '../../core/models/showed-on';
import { PoFormComponent } from '../../components/po-form/po-form.component';
import { switchMap, forkJoin, of } from 'rxjs';
import { TranslatePipe } from '../../core/pipes/translate.pipe';
import { TranslateKeyPipe } from '../../core/pipes/translate-key.pipe';

@Component({
  selector: 'app-po-edit',
  imports: [CommonModule, BsAlertModule, PoFormComponent, TranslatePipe, TranslateKeyPipe],
  templateUrl: './po-edit.component.html'
})
export default class PoEditComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly sparkService = inject(SparkService);
  private readonly cdr = inject(ChangeDetectorRef);

  colors = Color;
  entityType: EntityType | null = null;
  item: PersistentObject | null = null;
  type: string = '';
  id: string = '';
  formData: Record<string, any> = {};
  validationErrors: ValidationError[] = [];
  isSaving = false;

  ngOnInit(): void {
    this.route.paramMap.pipe(
      switchMap(params => {
        this.type = params.get('type') || '';
        this.id = params.get('id') || '';
        return forkJoin({
          entityType: this.sparkService.getEntityTypes().pipe(
            switchMap(types => of(types.find(t => t.id === this.type || t.alias === this.type) || null))
          ),
          item: this.sparkService.get(this.type, this.id)
        });
      })
    ).subscribe({
      next: result => {
        this.entityType = result.entityType;
        this.item = result.item;
        this.initFormData();
        this.cdr.detectChanges();
      },
      error: (error: HttpErrorResponse) => {
        this.validationErrors = [{
          attributeName: '',
          errorMessage: { en: error.error?.error || error.message || 'An unexpected error occurred' },
          ruleType: 'error'
        }];
        this.cdr.detectChanges();
      }
    });
  }

  initFormData(): void {
    this.formData = {};
    this.getEditableAttributes().forEach(attr => {
      const itemAttr = this.item?.attributes.find(a => a.name === attr.name);
      if (attr.dataType === 'Reference') {
        this.formData[attr.name] = itemAttr?.value ?? null;
      } else if (attr.dataType === 'AsDetail') {
        this.formData[attr.name] = itemAttr?.value ?? (attr.isArray ? [] : {});
      } else if (attr.dataType === 'boolean') {
        this.formData[attr.name] = itemAttr?.value ?? false;
      } else {
        this.formData[attr.name] = itemAttr?.value ?? '';
      }
    });
  }

  getEditableAttributes() {
    return this.entityType?.attributes
      .filter(a => a.isVisible && !a.isReadOnly && hasShowedOnFlag(a.showedOn, ShowedOn.PersistentObject))
      .sort((a, b) => a.order - b.order) || [];
  }

  onSave(): void {
    if (!this.entityType || !this.item) return;

    // Clear previous validation errors
    this.validationErrors = [];
    this.isSaving = true;

    const attributes: PersistentObjectAttribute[] = this.item.attributes.map(attr => {
      const editableAttr = this.getEditableAttributes().find(a => a.name === attr.name);
      const newValue = editableAttr ? this.formData[attr.name] : attr.value;
      return {
        ...attr,
        value: newValue,
        isValueChanged: editableAttr ? newValue !== attr.value : false
      };
    });

    const po: Partial<PersistentObject> = {
      id: this.item.id,
      name: this.formData['Name'] || this.item.name,
      objectTypeId: this.entityType.id,
      attributes
    };

    this.sparkService.update(this.type, this.id, po).subscribe({
      next: () => {
        this.isSaving = false;
        this.router.navigate(['/po', this.type, this.id]);
      },
      error: (error: HttpErrorResponse) => {
        this.isSaving = false;
        if (error.status === 400 && error.error?.errors) {
          this.validationErrors = error.error.errors;
        } else {
          this.validationErrors = [{
            attributeName: '',
            errorMessage: { en: error.message || 'An unexpected error occurred' },
            ruleType: 'error'
          }];
        }
        this.cdr.detectChanges();
      },
      complete: () => {
        this.isSaving = false;
        this.cdr.detectChanges();
      }
    });
  }

  getGeneralErrors(): ValidationError[] {
    return this.validationErrors.filter(e => !e.attributeName);
  }

  onCancel(): void {
    this.router.navigate(['/po', this.type, this.id]);
  }
}
