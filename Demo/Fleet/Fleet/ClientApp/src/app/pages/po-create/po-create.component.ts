import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsContainerComponent } from '@mintplayer/ng-bootstrap/container';

import { SparkService, EntityType, PersistentObject, PersistentObjectAttribute, ValidationError, ShowedOn, hasShowedOnFlag, SparkPoFormComponent, ResolveTranslationPipe, TranslateKeyPipe } from '@mintplayer/ng-spark';

@Component({
  selector: 'app-po-create',
  imports: [CommonModule, BsAlertComponent, BsCardComponent, BsCardHeaderComponent, BsContainerComponent, SparkPoFormComponent, ResolveTranslationPipe, TranslateKeyPipe],
  templateUrl: './po-create.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export default class PoCreateComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly sparkService = inject(SparkService);

  colors = Color;
  entityType = signal<EntityType | null>(null);
  type = signal('');
  formData = signal<Record<string, any>>({});
  validationErrors = signal<ValidationError[]>([]);
  isSaving = signal(false);

  constructor() {
    this.route.paramMap.subscribe(params => this.onParamsChange(params));
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

  generalErrors = computed(() => this.validationErrors().filter(e => !e.attributeName));

  onCancel(): void {
    window.history.back();
  }
}
