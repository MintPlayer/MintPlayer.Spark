import { ChangeDetectionStrategy, Component, computed, inject, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsContainerComponent } from '@mintplayer/ng-bootstrap/container';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { SparkService, SparkLanguageService } from '@mintplayer/ng-spark/services';
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
  RefreshOverlay,
  applyOverlay,
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
  private readonly language = inject(SparkLanguageService);

  saved = output<PersistentObject>();
  cancelled = output<void>();

  colors = Color;
  entityType = signal<EntityType | null>(null);
  item = signal<PersistentObject | null>(null);
  type = '';
  id = '';
  formData = signal<Record<string, any>>({});
  /**
   * Bound two-way to the form, which is where refreshes land. Read by
   * {@link getEditableAttributes} so the save sees the object the user was actually shown.
   */
  refreshOverlay = signal<RefreshOverlay>({});
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

  /**
   * Resolves a nested AsDetail type by CLR name, for rebuilding the nested PO wire shape on save.
   *
   * ⚠️ <b>`detailTypes` first, catalogue second.</b> The catalogue from `/spark/types` is
   * <b>Query-gated</b>, and an AsDetail row type usually has no rights of its own — nobody grants
   * `Query/GateSettings`, because a gate is edited through the Repository that owns it. So the
   * catalogue does not contain it, the resolver returned undefined, and the save quietly took the
   * scalar branch: the attribute went out as a raw dict under `value` instead of a nested PO under
   * `object`, the server could not map it, and `EntityMapper`'s conversion `catch` swallowed the
   * failure. The save reported success and wrote nothing.
   * <para>
   * `detailTypes` is the parent's own copy of its row types, carried on the type definition for
   * exactly this reason (#385 fixed the rendering half; this is the save half). It is gated on the
   * parent's right, which is the right that governs editing the row anyway.
   * </para>
   */
  private resolveEntityType(): EntityTypeResolver {
    const cache = this.allEntityTypes();
    const detailTypes = this.entityType()?.detailTypes ?? [];
    return (clrName: string) =>
      detailTypes.find(t => t.clrType === clrName) ?? cache.find(t => t.clrType === clrName);
  }

  /**
   * The attributes this page will read values from when it builds the save.
   *
   * ⚠️ <b>Overlaid before filtering, not after loading.</b> A refresh hook can reveal an attribute
   * that was hidden when the object was loaded, and the ordinary reason it does so is that the
   * attribute has just become required. Filtering on the loaded state left such an attribute out of
   * both {@link initFormData} and the save payload, so the user filled in a field whose value was
   * then dropped on the floor and refused by the server as missing — with no way out of the form.
   * The overlay is the form's, bound two-way, so the two halves cannot drift again.
   */
  getEditableAttributes() {
    const overlay = this.refreshOverlay();
    return this.entityType()?.attributes
      .map(a => applyOverlay(a, overlay[a.name]))
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

      // `in`, not a truthiness or `?? attr.value` check: an attribute the refresh revealed has no
      // slot until its control writes one, and an attribute the user cleared has a slot holding ''.
      // Coalescing would resurrect the loaded value on exactly the edit that removed it.
      const formData = this.formData();
      const rawValue = editableAttr && attr.name in formData ? formData[attr.name] : attr.value;

      if (editableAttr && isDateDataType(editableAttr.dataType)) {
        // Back out of the control's bare wall clock into a complete ISO-8601 instant, carrying the
        // viewer's offset for the entered date.
        const converted = fromDateInputValue(editableAttr.dataType, rawValue);
        // Compare by instant, not by text: the round trip rewrites the offset to the viewer's even
        // when nothing was edited.
        const changed = !wireDatesEqual(converted, attr.value);
        return {
          ...attr,
          // When the value did not actually change, send back exactly what was loaded. Sending the
          // re-converted string would preserve the instant but rewrite the stored offset to the
          // viewer's, so merely opening a record and saving it would relabel a Seattle registration
          // as a Brussels one. The offset is not business data (so the instant is what we guarantee),
          // but there is no reason to discard it on a save that changed nothing.
          value: changed ? converted : attr.value,
          isValueChanged: changed,
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
      // Send back the token this object was loaded with. Left out, the server skips the
      // concurrency check entirely -- it is opt-in by presence -- and a save over somebody else's
      // edit succeeds silently. Left as undefined when the server sent none, which drops the key
      // from the JSON and restores exactly the old behaviour rather than sending an empty string
      // that could never match.
      etag: currentItem.etag,
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
      } else if (error.status === 409) {
        // Somebody saved this record between the load and this save. The server's own body says
        // only "Concurrency conflict" -- deliberately, since the real message carries the change
        // vector -- which is accurate, untranslated, and tells the user nothing to do about it.
        // The form keeps its values, so the typing is not lost.
        this.validationErrors.set([{
          attributeName: '',
          errorMessage: { en: this.language.t('common.concurrencyConflict') },
          ruleType: 'error'
        }]);
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
