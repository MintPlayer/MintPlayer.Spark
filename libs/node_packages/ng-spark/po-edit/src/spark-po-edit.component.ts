import { ChangeDetectionStrategy, Component, computed, inject, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsContainerComponent } from '@mintplayer/ng-bootstrap/container';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { SparkService } from '@mintplayer/ng-spark/services';
import { SparkPoFormComponent } from '@mintplayer/ng-spark/po-form';
import { TranslateKeyPipe, ResolveTranslationPipe } from '@mintplayer/ng-spark/pipes';
import {
  EntityType,
  PersistentObject,
  PersistentObjectAttribute,
  ValidationError,
  ShowedOn,
  hasShowedOnFlag,
  nestedPoToDict,
  dictToNestedPo,
  EntityTypeResolver,
  isDateDataType,
  toDateInputValue,
  fromDateInputValue,
  wireDatesEqual,
} from '@mintplayer/ng-spark/models';

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
  // Cached list of every entity type — needed by the AsDetail save path to resolve
  // nested type schemas when rebuilding the nested PO wire shape from the flat form dict.
  private allEntityTypes = signal<EntityType[]>([]);
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
      this.allEntityTypes.set(types);
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
        // Server emits nested PO(s) in attr.object / attr.objects. Flatten back into the
        // Record<string, any> shape the form has always used so the rest of the component
        // tree stays unchanged.
        if (attr.isArray) {
          data[attr.name] = (itemAttr?.objects ?? []).map(po => nestedPoToDict(po));
        } else {
          data[attr.name] = itemAttr?.object ? nestedPoToDict(itemAttr.object) : {};
        }
      } else if (attr.dataType === 'boolean' && !attr.lookupReferenceType) {
        // `?? false` only for a real two-state checkbox. A lookup-backed boolean has a third state
        // that is expressed as the absence of a value, and coercing it here destroyed that BEFORE
        // the control rendered: merely opening the form and saving turned "unset" into an explicit
        // false, permanently and with nothing shown to the user.
        data[attr.name] = itemAttr?.value ?? false;
      } else if (isDateDataType(attr.dataType)) {
        // The wire carries a full ISO-8601 instant with an offset; a native date/time control accepts
        // only a bare local wall clock. Assigning the wire value directly does not fail loudly -- the
        // control just renders blank, and saving the untouched form writes that blank back.
        data[attr.name] = toDateInputValue(attr.dataType, itemAttr?.value);
      } else {
        data[attr.name] = itemAttr?.value ?? '';
      }
    });
    this.formData.set(data);
  }

  private resolveEntityType(): EntityTypeResolver {
    const cache = this.allEntityTypes();
    return (clrName: string) => cache.find(t => t.clrType === clrName);
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

    const resolver = this.resolveEntityType();
    const attributes: PersistentObjectAttribute[] = currentItem.attributes.map(attr => {
      const editableAttr = this.getEditableAttributes().find(a => a.name === attr.name);

      // AsDetail: formData[name] is a flat dict (single) or array of dicts (array). Rebuild
      // the nested PO wire shape so the server's polymorphic converter hydrates it into a
      // PersistentObjectAttributeAsDetail.
      if (editableAttr?.dataType === 'AsDetail' && editableAttr.asDetailType) {
        const nestedType = resolver(editableAttr.asDetailType);
        if (nestedType) {
          const raw = this.formData()[attr.name];
          if (editableAttr.isArray) {
            const items: any[] = Array.isArray(raw) ? raw : [];
            return {
              ...attr,
              value: null,
              object: null,
              objects: items.map(item => dictToNestedPo((item ?? {}) as Record<string, any>, nestedType, resolver)),
              asDetailType: editableAttr.asDetailType,
              isValueChanged: true,
            };
          }
          return {
            ...attr,
            value: null,
            object: raw ? dictToNestedPo(raw as Record<string, any>, nestedType, resolver) : null,
            objects: null,
            asDetailType: editableAttr.asDetailType,
            isValueChanged: true,
          };
        }
      }

      const rawValue = editableAttr ? this.formData()[attr.name] : attr.value;

      if (editableAttr && isDateDataType(editableAttr.dataType)) {
        // Back out of the control's bare wall clock into a complete ISO-8601 instant, carrying the
        // viewer's offset for the entered date.
        const newValue = fromDateInputValue(editableAttr.dataType, rawValue);
        return {
          ...attr,
          value: newValue,
          // By instant, not by text: the round trip rewrites the offset even when nothing was edited.
          isValueChanged: !wireDatesEqual(newValue, attr.value),
        };
      }

      const newValue = rawValue;
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
      // ⚠️ The errors live under `result`, because every endpoint wraps its body in a
      // ClientOperationEnvelope: `{ result: { errors: [...] }, operations: [] }`. Reading
      // `error.error.errors` matched nothing, so this branch was dead and EVERY save-time
      // validation failure fell through to the raw Angular HTTP string below.
      const errors = error.error?.result?.errors ?? error.error?.errors;
      if (error.status === 400 && errors) {
        this.validationErrors.set(errors);
      } else {
        this.validationErrors.set([{
          attributeName: '',
          // Prefer the server's own message, as the load path already does. A refusal arrives as
          // 404 by design (an anti-oracle measure), and `error.message` renders that as
          // "Http failure response for /spark/po/...: 404 Not Found" inside the validation summary.
          errorMessage: { en: error.error?.result?.error || error.error?.error || error.message || 'An unexpected error occurred' },
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
