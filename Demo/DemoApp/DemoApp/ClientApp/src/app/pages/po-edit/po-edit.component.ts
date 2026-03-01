import { ChangeDetectionStrategy, Component, computed, inject, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsContainerComponent } from '@mintplayer/ng-bootstrap/container';
import {
  SparkService, SparkPoFormComponent,
  TranslateKeyPipe, ResolveTranslationPipe,
  EntityType, PersistentObject, PersistentObjectAttribute, ValidationError,
  ShowedOn, hasShowedOnFlag
} from '@mintplayer/ng-spark';
import { firstValueFrom } from 'rxjs';

@Component({
  selector: 'app-po-edit',
  imports: [CommonModule, BsAlertComponent, BsCardComponent, BsCardHeaderComponent, BsContainerComponent, SparkPoFormComponent, TranslateKeyPipe, ResolveTranslationPipe],
  templateUrl: './po-edit.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export default class PoEditComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly sparkService = inject(SparkService);

  colors = Color;
  entityType = signal<EntityType | null>(null);
  item = signal<PersistentObject | null>(null);
  type = signal('');
  id = signal('');
  formData = signal<Record<string, any>>({});
  validationErrors = signal<ValidationError[]>([]);
  isSaving = signal(false);

  async ngOnInit(): Promise<void> {
    const params = await firstValueFrom(this.route.paramMap);
    this.type.set(params.get('type') || '');
    this.id.set(params.get('id') || '');

    try {
      const [types, itemResult] = await Promise.all([
        this.sparkService.getEntityTypes(),
        this.sparkService.get(this.type(), this.id())
      ]);

      this.entityType.set(types.find(t => t.id === this.type() || t.alias === this.type()) || null);
      this.item.set(itemResult);
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
    this.getEditableAttributes().forEach(attr => {
      const itemAttr = this.item()?.attributes.find(a => a.name === attr.name);
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
    if (!this.entityType() || !this.item()) return;

    this.validationErrors.set([]);
    this.isSaving.set(true);

    const attributes: PersistentObjectAttribute[] = this.item()!.attributes.map(attr => {
      const editableAttr = this.getEditableAttributes().find(a => a.name === attr.name);
      const newValue = editableAttr ? this.formData()[attr.name] : attr.value;
      return {
        ...attr,
        value: newValue,
        isValueChanged: editableAttr ? newValue !== attr.value : false
      };
    });

    const po: Partial<PersistentObject> = {
      id: this.item()!.id,
      name: this.formData()['Name'] || this.item()!.name,
      objectTypeId: this.entityType()!.id,
      attributes
    };

    try {
      await this.sparkService.update(this.type(), this.id(), po);
      this.isSaving.set(false);
      this.router.navigate(['/po', this.type(), this.id()]);
    } catch (e) {
      const error = e as HttpErrorResponse;
      this.isSaving.set(false);
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
    this.router.navigate(['/po', this.type(), this.id()]);
  }
}
