import { ChangeDetectionStrategy, Component, OnInit, computed, forwardRef, inject, input, output, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsModalComponent, BsModalHeaderDirective, BsModalFooterDirective } from '@mintplayer/ng-bootstrap/modal';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { SparkService } from '@mintplayer/ng-spark/services';
import { ResolveTranslationPipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import {
  EntityType,
  EntityTypeResolver,
  PersistentObject,
  RefreshOverlay,
  ValidationError,
  applyOverlay,
  dictToNestedPo,
  nestedPoToDict,
  overlayFromResponse,
} from '@mintplayer/ng-spark/models';
import { SparkPoFormComponent } from './spark-po-form.component';

/** What an **Add new** from a Reference picker opens the popup with (reference add-new PRD D16). */
export interface AddNewReferenceRequest {
  /** The referenced type: the type of the object being created. */
  target: EntityType;
  /** The owner's entity type id: the type that declares the Reference attribute. */
  ownerType: string;
  /** The owner's Reference attribute the picker edits. */
  referenceAttribute: string;
  /** The owner as it is now, with its `parent` chain (D12). */
  parent: PersistentObject;
  /** The picker's search term when [+] was clicked (D17); the server prefills the attribute's `defaultAttribute`. */
  searchTerm?: string;
}

/**
 * The **Add new** popup: the referenced type's blank object from `/po/new` (its `OnNewAsync`, with the
 * picker context), edited in the shared form and created with `/po/create`. It closes with the created
 * object, or `null` when cancelled (R9). Opened through {@link SparkPoNewModalService}, one instance per
 * call, so a popup's own form can open another on top (D16).
 */
@Component({
  selector: 'spark-po-new-modal',
  imports: [
    BsModalComponent, BsModalHeaderDirective, BsModalFooterDirective, BsButtonTypeDirective, BsSpinnerComponent,
    // The form opens this popup through the service, which renders this component: a cycle between
    // the two files, broken here.
    forwardRef(() => SparkPoFormComponent),
    TranslateKeyPipe, ResolveTranslationPipe,
  ],
  template: `
    <bs-modal [open]="isOpen()" (openChange)="!$event && cancel()">
      <ng-container *bsModalHeader>{{ 'common.new' | t }} {{ request().target.label | resolveTranslation:request().target.name }}</ng-container>
      @if (isOpen()) {
        @for (error of generalErrors(); track $index) {
          <div class="text-danger mb-2 spark-po-new-error">{{ error.errorMessage | resolveTranslation }}</div>
        }
        @if (cancelledByServer()) {
          <div class="text-warning mb-2">{{ 'common.createCancelled' | t }}</div>
        }
        @if (entityType(); as et) {
          <spark-po-form
            [entityType]="et"
            [objectTypeId]="et.id"
            [(formData)]="formData"
            [(refreshOverlay)]="refreshOverlay"
            [validationErrors]="validationErrors()"
            [ownerParent]="request().parent"
            [showButtons]="false">
          </spark-po-form>
        } @else if (!loadFailed()) {
          <div class="text-center p-3"><bs-spinner /></div>
        }
      }
      <ng-container *bsModalFooter>
        <button type="button" class="spark-po-new-save" [color]="colors.primary" [disabled]="!entityType() || isSaving()" (click)="save()">
          {{ 'common.save' | t }}
        </button>
        <button type="button" class="spark-po-new-cancel" [color]="colors.secondary" (click)="cancel()">{{ 'common.cancel' | t }}</button>
      </ng-container>
    </bs-modal>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkPoNewModalComponent implements OnInit {
  private readonly sparkService = inject(SparkService);

  request = input.required<AddNewReferenceRequest>();
  /** The created object, or `null` when the popup was cancelled. Emitted once. */
  closed = output<PersistentObject | null>();

  colors = Color;
  isOpen = signal(true);
  entityType = signal<EntityType | null>(null);
  formData = signal<Record<string, any>>({});
  refreshOverlay = signal<RefreshOverlay>({});
  validationErrors = signal<ValidationError[]>([]);
  isSaving = signal(false);
  loadFailed = signal(false);
  /** A server interceptor answered the create with "nothing created" (204, R5): the popup stays open. */
  cancelledByServer = signal(false);
  private allEntityTypes: EntityType[] = [];

  /** Errors the form cannot show beside a field: none named, or on an attribute it does not draw. */
  generalErrors = computed(() => {
    const drawn = new Set((this.entityType()?.attributes ?? []).map(a => a.name));
    return this.validationErrors().filter(e => !e.attributeName || !drawn.has(e.attributeName.split(/[.[]/)[0]));
  });

  ngOnInit(): void {
    void this.load();
  }

  private async load(): Promise<void> {
    const request = this.request();
    try {
      const [types, definition, po] = await Promise.all([
        this.sparkService.getEntityTypes(),
        // The create form's shape (#264): what the caller may not create is absent.
        this.sparkService.getEntityType(request.target.id, 'new'),
        this.sparkService.newObject(request.target.id, {
          parentType: request.ownerType,
          referenceAttribute: request.referenceAttribute,
          parent: request.parent,
          parameters: request.searchTerm ? { searchTerm: request.searchTerm } : undefined,
        }),
      ]);
      this.allEntityTypes = types;
      // What OnNewAsync decided (hidden, read-only, required) applies from the first render.
      this.refreshOverlay.set(overlayFromResponse(po));
      this.formData.set(nestedPoToDict(po));
      this.entityType.set(definition);
    } catch (e) {
      this.loadFailed.set(true);
      this.validationErrors.set(errorsOf(e, 'The new item could not be prepared.'));
    }
  }

  async save(): Promise<void> {
    const entityType = this.entityType();
    if (!entityType || this.isSaving()) return;

    this.isSaving.set(true);
    this.validationErrors.set([]);
    this.cancelledByServer.set(false);

    const resolver: EntityTypeResolver = clrName =>
      this.allEntityTypes.find(t => t.clrType === clrName) ?? entityType.detailTypes?.find(t => t.clrType === clrName);
    const overlay = this.refreshOverlay();
    const writable = new Set(entityType.attributes
      .map(a => applyOverlay(a, overlay[a.name]))
      .filter(a => !a.isReadOnly)
      .map(a => a.name));
    const built = dictToNestedPo(this.formData(), entityType, resolver);
    const po: Partial<PersistentObject> = {
      name: entityType.name,
      objectTypeId: entityType.id,
      attributes: built.attributes.filter(a => writable.has(a.name)),
    };

    try {
      const created = await this.sparkService.create(entityType.id, po);
      this.isSaving.set(false);
      if (!created) {
        this.cancelledByServer.set(true);
        return;
      }
      this.finish(created);
    } catch (e) {
      this.isSaving.set(false);
      this.validationErrors.set(errorsOf(e, 'An unexpected error occurred'));
    }
  }

  cancel(): void {
    this.finish(null);
  }

  private finish(result: PersistentObject | null): void {
    if (!this.isOpen()) return;
    this.isOpen.set(false);
    this.closed.emit(result);
  }
}

/** The errors of a refused request, read from the envelope as po-create reads them. */
function errorsOf(e: unknown, fallback: string): ValidationError[] {
  const error = e as HttpErrorResponse;
  const errors = error?.error?.result?.errors ?? error?.error?.errors;
  if (error?.status === 400 && errors) return errors;
  return [{ attributeName: '', errorMessage: { en: error?.message || fallback }, ruleType: 'error' } as ValidationError];
}
