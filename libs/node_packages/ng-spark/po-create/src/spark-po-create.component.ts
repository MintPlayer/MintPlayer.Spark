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
  dictToNestedPo,
  EntityTypeResolver,
  isDateDataType,
  fromDateInputValue,
  toDateInputValue,
  RefreshOverlay,
  applyOverlay,
  overlayFromResponse,
} from '@mintplayer/ng-spark/models';

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

  saved = output<PersistentObject>();
  cancelled = output<void>();

  colors = Color;
  entityType = signal<EntityType | null>(null);
  type = signal('');
  formData = signal<Record<string, any>>({});
  /** Bound two-way to the form. See {@link getEditableAttributes}. */
  refreshOverlay = signal<RefreshOverlay>({});
  validationErrors = signal<ValidationError[]>([]);
  isSaving = signal(false);
  private allEntityTypes = signal<EntityType[]>([]);
  /**
   * The errors the form cannot show next to a field: those with no attribute, and those on an attribute the
   * form does not draw (#264, G6). Those used to be swallowed; Vidyano promotes them to a notification too.
   */
  generalErrors = computed(() => {
    const overlay = this.refreshOverlay();
    const drawn = new Set((this.entityType()?.attributes ?? [])
      .map(a => applyOverlay(a, overlay[a.name]))
      .filter(a => hasShowedOnFlag(a.showedOn, ShowedOn.PersistentObject))
      .map(a => a.name));
    // A row error names its AsDetail attribute first ("Jobs[0].Title", "Jobs.Title").
    return this.validationErrors().filter(e => !e.attributeName || !drawn.has(e.attributeName.split(/[.[]/)[0]));
  });
  /**
   * The parent a sub-query card's New passed in the URL (`parentId`, `parentType`, `queryId`), or
   * null. Forwarded to the form so a Reference attribute's option query runs under the object the
   * New was started from — the same parent `/po/new` receives.
   */
  subQueryParent = signal<{ parentId: string; parentType: string; queryId: string } | null>(null);

  constructor() {
    this.route.paramMap.pipe(takeUntilDestroyed()).subscribe(params => this.onParamsChange(params));
  }

  private async onParamsChange(params: any): Promise<void> {
    this.type.set(params.get('type') || '');
    const types = await this.sparkService.getEntityTypes();
    const listed = types.find(t => t.id === this.type() || t.alias === this.type()) || null;
    // The create form's own shape (#264, G1/G2): what the caller may not create is absent, and an
    // edit-only deny does not make a field read-only here.
    const entityType = listed ? await this.sparkService.getEntityType(listed.id, 'new') : null;
    this.allEntityTypes.set(types);
    this.initFormData(entityType);
    if (entityType) await this.applyServerDefaults(entityType);
    this.entityType.set(entityType);
  }

  /**
   * The blank object comes from the server (#460, D19): `/po/new` runs the type's `OnNewAsync`, so a
   * default a hook sets — and, for a New started from a sub-query, the reference to the parent the
   * base hook fills — reaches the form. The sub-query card passes the parent as query parameters
   * (`parentId`, `parentType`, `queryId`), so it survives the navigation and a reload of this page.
   *
   * A refusal (the parent is gone, or not the caller's to see) is shown and the client-side blank
   * form stays usable, exactly as it was before the round-trip existed.
   */
  private async applyServerDefaults(entityType: EntityType): Promise<void> {
    const query = this.route.snapshot.queryParamMap;
    const parentId = query.get('parentId');
    const parentType = query.get('parentType');
    const queryId = query.get('queryId');
    const subQueryParent = parentId && parentType && queryId ? { parentId, parentType, queryId } : null;
    this.subQueryParent.set(subQueryParent);

    try {
      const po = await this.sparkService.newObject(this.type(), subQueryParent ?? undefined);
      // What OnNewAsync decided about the form — the runtime showedOn, isRequired, isReadOnly — applies from
      // the first render, exactly as a refresh's would (#264, G5/G7). Re-initialised, so an attribute the
      // hook revealed gets its slot.
      if (po) {
        this.refreshOverlay.set(overlayFromResponse(po));
        this.initFormData(entityType);
      }
      const editable = new Map(this.getEditableAttributes(entityType).map(a => [a.name, a] as const));
      const data = { ...this.formData() };
      for (const attr of po?.attributes ?? []) {
        const definition = editable.get(attr.name);
        if (!definition || attr.value === null || attr.value === undefined || definition.dataType === 'AsDetail') continue;
        data[attr.name] = isDateDataType(definition.dataType)
          ? toDateInputValue(definition.dataType, attr.value)
          : attr.value;
      }
      this.formData.set(data);
    } catch (e) {
      const error = e as HttpErrorResponse;
      const errors = error.error?.result?.errors ?? error.error?.errors;
      this.validationErrors.set(errors ?? [{
        attributeName: '',
        errorMessage: { en: error.message || 'The new item could not be prepared.' },
        ruleType: 'error'
      }]);
    }
  }

  initFormData(entityType: EntityType | null = this.entityType()): void {
    const data: Record<string, any> = {};
    this.getEditableAttributes(entityType).forEach(attr => {
      if (attr.dataType === 'Reference') {
        data[attr.name] = null;
      } else if (attr.dataType === 'AsDetail') {
        data[attr.name] = attr.isArray ? [] : {};
      } else if (attr.dataType === 'boolean' && !attr.lookupReferenceType) {
        // Same reason as spark-po-edit: a lookup-backed boolean's third state is the absence of a
        // value, and seeding `false` here would make every new object explicitly opted out.
        data[attr.name] = false;
      } else {
        data[attr.name] = '';
      }
    });
    this.formData.set(data);
  }

  /**
   * ⚠️ Overlaid before filtering — same reason as <c>spark-po-edit</c>, and worse here: this list is
   * the <em>only</em> source of the create payload, so an attribute a refresh hook revealed was not
   * merely sent stale, it was absent from the new object entirely.
   */
  getEditableAttributes(entityType: EntityType | null = this.entityType()) {
    const overlay = this.refreshOverlay();
    return entityType?.attributes
      .map(a => applyOverlay(a, overlay[a.name]))
      .filter(a => !a.isReadOnly && hasShowedOnFlag(a.showedOn, ShowedOn.PersistentObject))
      .sort((a, b) => a.order - b.order) || [];
  }

  async onSave(): Promise<void> {
    if (!this.entityType()) return;

    this.validationErrors.set([]);
    this.isSaving.set(true);

    const resolver: EntityTypeResolver = (clrName) => this.allEntityTypes().find(t => t.clrType === clrName);
    const attributes: PersistentObjectAttribute[] = this.getEditableAttributes().map(attr => {
      const base: PersistentObjectAttribute = {
        id: attr.id,
        name: attr.name,
        // A date control hands back a bare local wall clock; the wire needs a complete ISO-8601
        // instant carrying the viewer's offset for the entered date.
        value: isDateDataType(attr.dataType)
          ? fromDateInputValue(attr.dataType, this.formData()[attr.name])
          : this.formData()[attr.name],
        dataType: attr.dataType,
        isArray: attr.isArray,
        isRequired: attr.isRequired,
        isReadOnly: attr.isReadOnly,
        isValueChanged: true,
        order: attr.order,
        rules: attr.rules,
      };

      // AsDetail: pack the flat form dict into nested PO wire shape. Server's polymorphic
      // converter ignores attr.value for AsDetail and reads attr.object / attr.objects.
      if (attr.dataType === 'AsDetail' && attr.asDetailType) {
        const nestedType = resolver(attr.asDetailType);
        if (nestedType) {
          const raw = this.formData()[attr.name];
          base.value = null;
          base.asDetailType = attr.asDetailType;
          if (attr.isArray) {
            const items: any[] = Array.isArray(raw) ? raw : [];
            base.objects = items.map(item => dictToNestedPo((item ?? {}) as Record<string, any>, nestedType, resolver));
            base.object = null;
          } else {
            base.object = raw ? dictToNestedPo(raw as Record<string, any>, nestedType, resolver) : null;
            base.objects = null;
          }
        }
      }
      return base;
    });

    const po: Partial<PersistentObject> = {
      name: this.formData()['Name'] || 'New Item',
      objectTypeId: this.entityType()!.id,
      attributes
    };

    try {
      const result = await this.sparkService.create(this.type(), po, this.subQueryParent() ?? undefined);
      this.isSaving.set(false);
      // A server interceptor cancelled the create (the user answered its prompt with Cancel): nothing was
      // created, so the form stays as it is.
      if (!result) return;
      this.saved.emit(result);
      this.router.navigate(['/po', this.type(), result.id]);
    } catch (e) {
      this.isSaving.set(false);
      const error = e as HttpErrorResponse;
      // The errors live under `result` (every endpoint wraps its body in a ClientOperationEnvelope:
      // `{ result: { errors: [...] }, operations: [] }`), as in po-edit. Reading only
      // `error.error.errors` showed every create-time refusal as the raw Angular HTTP string.
      const errors = error.error?.result?.errors ?? error.error?.errors;
      if (error.status === 400 && errors) {
        this.validationErrors.set(errors);
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
