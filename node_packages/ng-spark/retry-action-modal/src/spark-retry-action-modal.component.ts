import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsModalHostComponent, BsModalDirective, BsModalHeaderDirective, BsModalBodyDirective, BsModalFooterDirective } from '@mintplayer/ng-bootstrap/modal';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { RetryActionService, SparkService } from '@mintplayer/ng-spark/services';
import { SparkPoFormComponent } from '@mintplayer/ng-spark/po-form';
import {
  dictToNestedPo,
  EntityAttributeDefinition,
  EntityType,
  EntityTypeResolver,
  nestedPoToDict,
  PersistentObject,
  PersistentObjectAttribute,
} from '@mintplayer/ng-spark/models';

/**
 * Renders a retry-action popup. Before PRD §3 this component rendered title / message /
 * option buttons only and silently forwarded the incoming <c>persistentObject</c> back to
 * the server on submit — meaning any <c>Retry.Action(..., persistentObject)</c> flow had
 * no UI to actually edit the PO. This component now embeds the shared PO form so every
 * scalar / Reference / AsDetail attribute on the scaffolded Virtual PO is a real form
 * field, and the values the user fills in flow back to the server via
 * <c>RetryResult.PersistentObject</c>.
 */
@Component({
  selector: 'spark-retry-action-modal',
  imports: [CommonModule, BsModalHostComponent, BsModalDirective, BsModalHeaderDirective, BsModalBodyDirective, BsModalFooterDirective, BsButtonTypeDirective, SparkPoFormComponent],
  template: `
    <bs-modal [isOpen]="isOpen()" (isOpenChange)="!$event && onOption('Cancel')">
      <div *bsModal>
        <div bsModalHeader>
          <h5 class="modal-title">{{ retryActionService.payload()?.title }}</h5>
        </div>
        <div bsModalBody>
          @if (retryActionService.payload()?.message; as message) {
            <p>{{ message }}</p>
          }
          @if (entityType(); as et) {
            <spark-po-form
              [entityType]="et"
              [(formData)]="formData"
              [showButtons]="false">
            </spark-po-form>
          }
        </div>
        <div bsModalFooter>
          @for (option of retryActionService.payload()?.options; track option) {
            <button
              type="button"
              [color]="option === 'Cancel' ? colors.secondary : colors.primary"
              (click)="onOption(option)">
              {{ option }}
            </button>
          }
        </div>
      </div>
    </bs-modal>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class SparkRetryActionModalComponent {
  protected readonly retryActionService = inject(RetryActionService);
  private readonly sparkService = inject(SparkService);

  colors = Color;
  isOpen = computed(() => this.retryActionService.payload() !== null);

  /**
   * EntityType definition for the incoming PO — fetched lazily via SparkService so the
   * form knows which attributes are editable, their labels, rules, renderers, etc.
   * `null` when the payload has no persistentObject or its objectTypeId doesn't match
   * any registered entity type (renders the modal as a simple option picker).
   */
  entityType = signal<EntityType | null>(null);
  formData = signal<Record<string, any>>({});
  private allEntityTypes: EntityType[] = [];

  constructor() {
    // Reseed form state every time the retry service opens/closes the modal. Effect
    // cleanup isn't needed since `payload` is a signal and the component's own lifetime
    // is root-scoped.
    effect(() => {
      const payload = this.retryActionService.payload();
      if (!payload?.persistentObject) {
        this.entityType.set(null);
        this.formData.set({});
        return;
      }
      void this.seedForm(payload.persistentObject);
    });
  }

  private async seedForm(po: PersistentObject): Promise<void> {
    // Virtual POs used for retry prompts (e.g. ConfirmDeleteCar) typically have no
    // security.json grant — so `getEntityTypes()` filters them out for the current user
    // and the lookup-by-id would return null, leaving the form blank. The scaffolded PO
    // already carries full attribute metadata (label / dataType / rules / renderer / etc.),
    // so we synthesize an EntityType from the attributes directly and skip the HTTP
    // lookup altogether. `getEntityTypes()` is still fetched once (cached on the
    // component) because the embedded spark-po-form needs the full list to resolve
    // nested AsDetail / Reference types the retry PO might point at.
    if (this.allEntityTypes.length === 0) {
      try { this.allEntityTypes = await this.sparkService.getEntityTypes(); }
      catch { this.allEntityTypes = []; }
    }
    this.entityType.set(entityTypeFromPo(po));
    // Flatten the nested PO into the Record<string, any> shape the shared form uses
    // throughout the rest of ng-spark — same transformation po-edit applies.
    this.formData.set(nestedPoToDict(po));
  }

  onOption(option: string): void {
    const payload = this.retryActionService.payload();
    if (!payload) return;

    const populated = this.populatedPersistentObject(payload.persistentObject);
    this.retryActionService.respond({
      step: payload.step,
      option,
      persistentObject: populated,
    });
  }

  /**
   * Builds the PO the server sees under <c>Retry.Result.PersistentObject</c>. If the
   * form resolved an EntityType, rebuild from the schema + formData (identical to the
   * po-edit save path — AsDetail recursion included). Otherwise forward the incoming
   * PO unmodified so pre-§3 flows without editable attributes keep working.
   */
  private populatedPersistentObject(incoming: PersistentObject | undefined): PersistentObject | undefined {
    if (!incoming) return undefined;
    const type = this.entityType();
    if (!type) return incoming;

    const resolver: EntityTypeResolver = (clrName) => this.allEntityTypes.find(t => t.clrType === clrName);
    const rebuilt = dictToNestedPo(this.formData(), type, resolver);
    const populated: PersistentObject = {
      ...incoming,
      attributes: mergeAttributeMetadata(incoming.attributes ?? [], rebuilt.attributes),
    };
    return populated;
  }
}

/**
 * Builds a synthetic <see cref="EntityType"/> from the PO's own scaffolded attributes so
 * the embedded spark-po-form can render without having to locate the matching server-side
 * EntityType registration. Used for Virtual POs that are schema-registered but not
 * security-granted (retry-action popups).
 */
function entityTypeFromPo(po: PersistentObject): EntityType {
  return {
    id: po.objectTypeId,
    name: po.name,
    clrType: '', // Not needed by the form's rendering path; the PO's attributes carry the schema.
    displayAttribute: undefined,
    tabs: [],
    groups: [],
    attributes: (po.attributes ?? []).map(attrToDefinition),
    queries: [],
  };
}

function attrToDefinition(attr: PersistentObjectAttribute): EntityAttributeDefinition {
  return {
    id: attr.id ?? '',
    name: attr.name,
    label: attr.label,
    dataType: attr.dataType,
    isArray: attr.isArray,
    isRequired: attr.isRequired,
    isVisible: attr.isVisible,
    isReadOnly: attr.isReadOnly,
    order: attr.order,
    query: attr.query,
    asDetailType: attr.asDetailType,
    showedOn: attr.showedOn,
    rules: attr.rules ?? [],
    group: attr.group,
    renderer: attr.renderer,
    rendererOptions: attr.rendererOptions,
  };
}

/**
 * Keeps the server-issued id + metadata on each attribute while overlaying the user's
 * values from the rebuilt PO. Prevents the modal from accidentally dropping server-only
 * fields (e.g. rules, renderer options) that the form didn't need to know about.
 */
function mergeAttributeMetadata(
  incoming: PersistentObjectAttribute[],
  rebuilt: PersistentObjectAttribute[],
): PersistentObjectAttribute[] {
  const byName = new Map(rebuilt.map(a => [a.name, a]));
  return incoming.map(source => {
    const updated = byName.get(source.name);
    if (!updated) return source;
    return {
      ...source,
      value: updated.value,
      object: updated.object,
      objects: updated.objects,
      asDetailType: updated.asDetailType ?? source.asDetailType,
      isValueChanged: true,
    };
  });
}
