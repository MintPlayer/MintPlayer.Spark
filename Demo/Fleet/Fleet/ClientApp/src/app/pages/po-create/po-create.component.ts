import { ChangeDetectorRef, Component, inject, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertModule } from '@mintplayer/ng-bootstrap/alert';
import { BsCardModule } from '@mintplayer/ng-bootstrap/card';
import { BsContainerComponent } from '@mintplayer/ng-bootstrap/container';
import { SparkService } from '../../core/services/spark.service';
import { EntityType, PersistentObject, PersistentObjectAttribute, ValidationError } from '../../core/models';
import { ShowedOn, hasShowedOnFlag } from '../../core/models/showed-on';
import { PoFormComponent } from '../../components/po-form/po-form.component';
import { switchMap, of } from 'rxjs';
import { TranslatePipe } from '../../core/pipes/translate.pipe';
import { TranslateKeyPipe } from '../../core/pipes/translate-key.pipe';

@Component({
  selector: 'app-po-create',
  imports: [CommonModule, BsAlertModule, BsCardModule, BsContainerComponent, PoFormComponent, TranslatePipe, TranslateKeyPipe],
  templateUrl: './po-create.component.html'
})
export default class PoCreateComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly sparkService = inject(SparkService);
  private readonly cdr = inject(ChangeDetectorRef);

  colors = Color;
  entityType: EntityType | null = null;
  type: string = '';
  formData: Record<string, any> = {};
  validationErrors: ValidationError[] = [];
  isSaving = false;

  ngOnInit(): void {
    this.route.paramMap.pipe(
      switchMap(params => {
        this.type = params.get('type') || '';
        return this.sparkService.getEntityTypes().pipe(
          switchMap(types => of(types.find(t => t.id === this.type || t.alias === this.type) || null))
        );
      })
    ).subscribe(entityType => {
      this.entityType = entityType;
      this.initFormData();
      this.cdr.detectChanges();
    });
  }

  initFormData(): void {
    this.formData = {};
    this.getEditableAttributes().forEach(attr => {
      if (attr.dataType === 'Reference') {
        this.formData[attr.name] = null;
      } else if (attr.dataType === 'AsDetail') {
        this.formData[attr.name] = attr.isArray ? [] : {};
      } else if (attr.dataType === 'boolean') {
        this.formData[attr.name] = false;
      } else {
        this.formData[attr.name] = '';
      }
    });
  }

  getEditableAttributes() {
    return this.entityType?.attributes
      .filter(a => a.isVisible && !a.isReadOnly && hasShowedOnFlag(a.showedOn, ShowedOn.PersistentObject))
      .sort((a, b) => a.order - b.order) || [];
  }

  onSave(): void {
    if (!this.entityType) return;

    // Clear previous validation errors
    this.validationErrors = [];
    this.isSaving = true;

    const attributes: PersistentObjectAttribute[] = this.getEditableAttributes().map(attr => ({
      id: attr.id,
      name: attr.name,
      value: this.formData[attr.name],
      dataType: attr.dataType,
      isRequired: attr.isRequired,
      isVisible: attr.isVisible,
      isReadOnly: attr.isReadOnly,
      isValueChanged: true,
      order: attr.order,
      rules: attr.rules
    }));

    const po: Partial<PersistentObject> = {
      name: this.formData['Name'] || 'New Item',
      objectTypeId: this.entityType.id,
      attributes
    };

    this.sparkService.create(this.type, po).subscribe({
      next: result => {
        this.isSaving = false;
        this.router.navigate(['/po', this.type, result.id]);
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
      }
    });
  }

  getGeneralErrors(): ValidationError[] {
    return this.validationErrors.filter(e => !e.attributeName);
  }

  onCancel(): void {
    window.history.back();
  }
}
