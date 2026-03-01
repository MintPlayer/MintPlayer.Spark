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
  selector: 'spark-po-create',
  imports: [CommonModule, BsAlertComponent, BsContainerComponent, BsSpinnerComponent, SparkPoFormComponent, ResolveTranslationPipe, TranslateKeyPipe],
  templateUrl: './spark-po-create.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class SparkPoCreateComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly sparkService = inject(SparkService);

  @ContentChildren(SparkFieldTemplateDirective) fieldTemplates!: QueryList<SparkFieldTemplateDirective>;

  saved = output<PersistentObject>();
  cancelled = output<void>();

  colors = Color;
  entityType = signal<EntityType | null>(null);
  type = signal('');
  formData = signal<Record<string, any>>({});
  validationErrors = signal<ValidationError[]>([]);
  isSaving = signal(false);
  generalErrors = computed(() => this.validationErrors().filter(e => !e.attributeName));

  constructor() {
    this.route.paramMap.pipe(takeUntilDestroyed()).subscribe(params => this.onParamsChange(params));
  }

  private async onParamsChange(params: any): Promise<void> {
    this.type.set(params.get('type') || '');
    const types = await this.sparkService.getEntityTypes();
    const entityType = types.find(t => t.id === this.type() || t.alias === this.type()) || null;
    this.entityType.set(entityType);
    this.initFormData();
  }

  initFormData(): void {
    const data: Record<string, any> = {};
    this.getEditableAttributes().forEach(attr => {
      if (attr.dataType === 'Reference') {
        data[attr.name] = null;
      } else if (attr.dataType === 'AsDetail') {
        data[attr.name] = attr.isArray ? [] : {};
      } else if (attr.dataType === 'boolean') {
        data[attr.name] = false;
      } else {
        data[attr.name] = '';
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
    if (!this.entityType()) return;

    this.validationErrors.set([]);
    this.isSaving.set(true);

    const attributes: PersistentObjectAttribute[] = this.getEditableAttributes().map(attr => ({
      id: attr.id,
      name: attr.name,
      value: this.formData()[attr.name],
      dataType: attr.dataType,
      isRequired: attr.isRequired,
      isVisible: attr.isVisible,
      isReadOnly: attr.isReadOnly,
      isValueChanged: true,
      order: attr.order,
      rules: attr.rules
    }));

    const po: Partial<PersistentObject> = {
      name: this.formData()['Name'] || 'New Item',
      objectTypeId: this.entityType()!.id,
      attributes
    };

    try {
      const result = await this.sparkService.create(this.type(), po);
      this.isSaving.set(false);
      this.saved.emit(result);
      this.router.navigate(['/po', this.type(), result.id]);
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
    window.history.back();
  }
}
