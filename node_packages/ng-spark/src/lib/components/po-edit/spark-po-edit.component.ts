import { ChangeDetectionStrategy, Component, computed, ContentChildren, inject, output, QueryList, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsContainerComponent } from '@mintplayer/ng-bootstrap/container';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { SparkService } from '../../services/spark.service';
import { SparkPoFormComponent } from '../po-form/spark-po-form.component';
import { TranslateKeyPipe } from '../../pipes/translate-key.pipe';
import { ResolveTranslationPipe } from '../../pipes/resolve-translation.pipe';
import { SparkFieldTemplateDirective } from '../../directives/spark-field-template.directive';
import { EntityType } from '../../models/entity-type';
import { PersistentObject } from '../../models/persistent-object';
import { PersistentObjectAttribute } from '../../models/persistent-object-attribute';
import { ValidationError } from '../../models/validation-error';
import { ShowedOn, hasShowedOnFlag } from '../../models/showed-on';

@Component({
  selector: 'spark-po-edit',
  imports: [CommonModule, BsAlertComponent, BsContainerComponent, BsSpinnerComponent, SparkPoFormComponent, ResolveTranslationPipe, TranslateKeyPipe],
  templateUrl: './spark-po-edit.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class SparkPoEditComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly sparkService = inject(SparkService);

  @ContentChildren(SparkFieldTemplateDirective) fieldTemplates!: QueryList<SparkFieldTemplateDirective>;

  saved = output<PersistentObject>();
  cancelled = output<void>();

  colors = Color;
  entityType = signal<EntityType | null>(null);
  item = signal<PersistentObject | null>(null);
  type = '';
  id = '';
  formData = signal<Record<string, any>>({});
  validationErrors = signal<ValidationError[]>([]);
  isSaving = signal(false);
  generalErrors = computed(() => this.validationErrors().filter(e => !e.attributeName));

  constructor() {
    this.route.paramMap.pipe(takeUntilDestroyed()).subscribe(params => this.onParamsChange(params));
  }

  private async onParamsChange(params: any): Promise<void> {
    this.type = params.get('type') || '';
    this.id = params.get('id') || '';

    try {
      const [types, item] = await Promise.all([
        this.sparkService.getEntityTypes(),
        this.sparkService.get(this.type, this.id)
      ]);

      const entityType = types.find(t => t.id === this.type || t.alias === this.type) || null;
      this.entityType.set(entityType);
      this.item.set(item);
      this.initFormData();
    } catch (e) {
      const error = e as HttpErrorResponse;
      this.validationErrors.set([{
        attributeName: '',
        errorMessage: { en: error.error?.error || error.message || 'An unexpected error occurred' },
        ruleType: 'error'
      }]);
    }
  }

  initFormData(): void {
    const data: Record<string, any> = {};
    const currentItem = this.item();
    this.getEditableAttributes().forEach(attr => {
      const itemAttr = currentItem?.attributes.find(a => a.name === attr.name);
      if (attr.dataType === 'Reference') {
        data[attr.name] = itemAttr?.value ?? null;
      } else if (attr.dataType === 'AsDetail') {
        data[attr.name] = itemAttr?.value ?? (attr.isArray ? [] : {});
      } else if (attr.dataType === 'boolean') {
        data[attr.name] = itemAttr?.value ?? false;
      } else {
        data[attr.name] = itemAttr?.value ?? '';
      }
    });
    this.formData.set(data);
  }

  getEditableAttributes() {
    return this.entityType()?.attributes
      .filter(a => a.isVisible && !a.isReadOnly && hasShowedOnFlag(a.showedOn, ShowedOn.PersistentObject))
      .sort((a, b) => a.order - b.order) || [];
  }

  async onSave(): Promise<void> {
    const currentItem = this.item();
    if (!this.entityType() || !currentItem) return;

    this.validationErrors.set([]);
    this.isSaving.set(true);

    const attributes: PersistentObjectAttribute[] = currentItem.attributes.map(attr => {
      const editableAttr = this.getEditableAttributes().find(a => a.name === attr.name);
      const newValue = editableAttr ? this.formData()[attr.name] : attr.value;
      return {
        ...attr,
        value: newValue,
        isValueChanged: editableAttr ? newValue !== attr.value : false
      };
    });

    const po: Partial<PersistentObject> = {
      id: currentItem.id,
      name: this.formData()['Name'] || currentItem.name,
      objectTypeId: this.entityType()!.id,
      attributes
    };

    try {
      const result = await this.sparkService.update(this.type, this.id, po);
      this.isSaving.set(false);
      this.saved.emit(result as PersistentObject);
      this.router.navigate(['/po', this.type, this.id]);
    } catch (e) {
      this.isSaving.set(false);
      const error = e as HttpErrorResponse;
      if (error.status === 400 && error.error?.errors) {
        this.validationErrors.set(error.error.errors);
      } else {
        this.validationErrors.set([{
          attributeName: '',
          errorMessage: { en: error.message || 'An unexpected error occurred' },
          ruleType: 'error'
        }]);
      }
    }
  }

  onCancel(): void {
    this.cancelled.emit();
    this.router.navigate(['/po', this.type, this.id]);
  }
}
