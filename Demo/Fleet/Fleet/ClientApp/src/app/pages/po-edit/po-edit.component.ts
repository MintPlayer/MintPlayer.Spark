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
  selector: 'app-po-edit',
  imports: [CommonModule, BsAlertComponent, BsCardComponent, BsCardHeaderComponent, BsContainerComponent, SparkPoFormComponent, ResolveTranslationPipe, TranslateKeyPipe],
  templateUrl: './po-edit.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export default class PoEditComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly sparkService = inject(SparkService);

  colors = Color;
  entityType = signal<EntityType | null>(null);
  item = signal<PersistentObject | null>(null);
  type = '';
  id = '';
  formData = signal<Record<string, any>>({});
  validationErrors = signal<ValidationError[]>([]);
  isSaving = signal(false);

  constructor() {
    this.route.paramMap.subscribe(params => this.onParamsChange(params));
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
      await this.sparkService.update(this.type, this.id, po);
      this.isSaving.set(false);
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

  generalErrors = computed(() => this.validationErrors().filter(e => !e.attributeName));

  onCancel(): void {
    this.router.navigate(['/po', this.type, this.id]);
  }
}
