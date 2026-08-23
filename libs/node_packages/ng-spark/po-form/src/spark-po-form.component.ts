import { ChangeDetectionStrategy, Component, computed, inject, input, model, output, signal, effect, Type } from '@angular/core';
import { CommonModule, NgComponentOutlet, NgTemplateOutlet } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { CdkDropList, CdkDrag, CdkDragHandle, CdkDragPreview, CdkDragDrop, moveItemInArray } from '@angular/cdk/drag-drop';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsGridComponent, BsGridRowDirective, BsGridColumnDirective, BsGridColDirective, BsColFormLabelDirective } from '@mintplayer/ng-bootstrap/grid';
import { BsInputGroupComponent } from '@mintplayer/ng-bootstrap/input-group';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { BsSelectComponent, BsSelectOption } from '@mintplayer/ng-bootstrap/select';
import { BsTreeSelectComponent, InMemoryTreeSelectProvider, TreeNode } from '@mintplayer/ng-bootstrap/tree-select';
import { BsModalHostComponent, BsModalDirective, BsModalHeaderDirective, BsModalBodyDirective, BsModalFooterDirective } from '@mintplayer/ng-bootstrap/modal';
import { BsCheckboxComponent } from '@mintplayer/ng-bootstrap/checkbox';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { BsTabControlComponent, BsTabPageComponent, BsTabPageHeaderDirective } from '@mintplayer/ng-bootstrap/tab-control';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { SparkService, SparkLanguageService } from '@mintplayer/ng-spark/services';
import {
  TranslateKeyPipe,
  ResolveTranslationPipe,
  InputTypePipe,
  LookupDisplayTypePipe,
  LookupOptionsPipe,
  AsDetailDisplayValuePipe,
  AsDetailTypePipe,
  AsDetailColumnsPipe,
  AsDetailCellValuePipe,
  CanCreateDetailRowPipe,
  CanDeleteDetailRowPipe,
  InlineRefOptionsPipe,
  ErrorForAttributePipe,
} from '@mintplayer/ng-spark/pipes';
import {
  ELookupDisplayType,
  EReferenceDisplayType,
  EntityPermissions,
  EntityType,
  EntityAttributeDefinition,
  AttributeTab,
  AttributeGroup,
  LookupReference,
  LookupReferenceValue,
  PersistentObject,
  ValidationError,
  ShowedOn,
  hasShowedOnFlag,
  resolveTranslation,
  RefreshOverlay,
  RefreshedOption,
  applyOverlay,
  overlayFromResponse,
  mergeRefreshValues,
  evaluateRules,
  RuleFailure,
} from '@mintplayer/ng-spark/models';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';
import { SPARK_ATTRIBUTE_RENDERERS, withDeclaredInputs } from '@mintplayer/ng-spark/renderers';
import { SparkReferencePickerComponent } from './spark-reference-picker.component';
import { SparkLookupPickerComponent } from './spark-lookup-picker.component';
import { RefreshCoordinator, triggersImmediately } from './refresh-coordinator';

@Component({
  selector: 'spark-po-form',
  imports: [CommonModule, NgTemplateOutlet, NgComponentOutlet, FormsModule, CdkDropList, CdkDrag, CdkDragHandle, CdkDragPreview, BsCardComponent, BsCardHeaderComponent, BsFormComponent, BsFormControlDirective, BsGridComponent, BsGridRowDirective, BsGridColumnDirective, BsGridColDirective, BsColFormLabelDirective, BsButtonTypeDirective, BsInputGroupComponent, BsSelectComponent, BsSelectOption, BsTreeSelectComponent, BsModalHostComponent, BsModalDirective, BsModalHeaderDirective, BsModalBodyDirective, BsModalFooterDirective, BsTableComponent, BsCheckboxComponent, BsSpinnerComponent, BsTabControlComponent, BsTabPageComponent, BsTabPageHeaderDirective, SparkIconComponent, SparkPoFormComponent, SparkReferencePickerComponent, SparkLookupPickerComponent, TranslateKeyPipe, ResolveTranslationPipe, InputTypePipe, LookupDisplayTypePipe, LookupOptionsPipe, AsDetailDisplayValuePipe, AsDetailTypePipe, AsDetailColumnsPipe, AsDetailCellValuePipe, CanCreateDetailRowPipe, CanDeleteDetailRowPipe, InlineRefOptionsPipe, ErrorForAttributePipe],
  templateUrl: './spark-po-form.component.html',
  // The CDK drag placeholder is a clone of the dragged row (so it keeps the exact row
  // height). Hide its contents but keep it occupying space, so the drop gap is blank and
  // the surrounding rows don't shift. ::ng-deep because the cloned node is styled by CDK.
  styles: [`:host ::ng-deep .cdk-drag-placeholder { visibility: hidden; }`],
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class SparkPoFormComponent {
  private readonly sparkService = inject(SparkService);
  private readonly translations = inject(SparkLanguageService);
  private readonly rendererRegistry = inject(SPARK_ATTRIBUTE_RENDERERS);

  entityType = input<EntityType | null>(null);
  formData = model<Record<string, any>>({});
  validationErrors = input<ValidationError[]>([]);
  showButtons = input(false);
  isSaving = input(false);
  parentId = input<string | undefined>(undefined);
  parentType = input<string | undefined>(undefined);

  save = output<void>();
  cancel = output<void>();

  /**
   * The type id to refresh against. Absent means refresh is unavailable — the form still renders and
   * edits normally, so a host that has not opted in loses nothing.
   */
  objectTypeId = input<string | undefined>(undefined);
  /** The id of the object being edited; absent for a create. */
  objectId = input<string | undefined>(undefined);

  /**
   * What the last refresh changed about each attribute's presentation, keyed by attribute name.
   *
   * Deliberately NOT folded back into `entityType`. All option loading hangs off one effect keyed on
   * `entityType` identity and `SparkService` caches nothing, so re-setting it would re-issue every
   * reference query and lookup fetch on every refresh; mutating it in place would not re-render at
   * all.
   */
  refreshOverlay = signal<RefreshOverlay>({});
  isRefreshing = signal(false);

  colors = Color;
  referenceOptions = signal<Record<string, PersistentObject[]>>({});

  // Multi-reference (Reference && isArray) editor state. Built from the same query
  // results as referenceOptions: a flat TreeNode list per attribute drives an
  // InMemoryTreeSelectProvider (the <bs-tree-select> data port), and a node lookup
  // resolves selected ids back to chip labels.
  referenceProviders = signal<Record<string, InMemoryTreeSelectProvider>>({});
  referenceNodes = signal<Record<string, Record<string, TreeNode>>>({});
  asDetailTypes = signal<Record<string, EntityType>>({});
  lookupReferenceOptions = signal<Record<string, LookupReference>>({});

  // Modal state for AsDetail object editing
  editingAsDetailAttr = signal<EntityAttributeDefinition | null>(null);
  asDetailFormData = signal<Record<string, any>>({});
  showAsDetailModal = signal(false);
  editingArrayIndex = signal<number | null>(null);

  // Permissions for array AsDetail entity types
  asDetailPermissions = signal<Record<string, EntityPermissions>>({});

  // Reference options for columns within array AsDetail types (keyed by parent attr name, then column name)
  asDetailReferenceOptions = signal<Record<string, Record<string, PersistentObject[]>>>({});

  // Reference/Lookup picking is owned by the standalone spark-reference-picker /
  // spark-lookup-picker components (per-instance modal state); the form just feeds them
  // options and writes the emitted value back into formData / the row.
  ELookupDisplayType = ELookupDisplayType;
  EReferenceDisplayType = EReferenceDisplayType;

  /**
   * Every attribute this form could ever need option data for — including ones the model hides,
   * because a refresh may reveal them.
   *
   * Read by the option-loading effect, and deliberately independent of `refreshOverlay`: the loaders
   * read this synchronously, so an overlay dependency here would make every refresh re-issue every
   * reference query and lookup fetch. That is the whole reason the overlay is a separate signal.
   */
  optionSourceAttributes = computed(() => {
    return this.entityType()?.attributes
      .filter(a => hasShowedOnFlag(a.showedOn, ShowedOn.PersistentObject))
      .sort((a, b) => a.order - b.order) || [];
  });

  editableAttributes = computed(() => {
    const overlay = this.refreshOverlay();
    return this.entityType()?.attributes
      .map(a => applyOverlay(a, overlay[a.name]))
      .filter(a => a.isVisible && !a.isReadOnly && hasShowedOnFlag(a.showedOn, ShowedOn.PersistentObject))
      .sort((a, b) => a.order - b.order) || [];
  });

  private static readonly DEFAULT_TAB: AttributeTab = { id: '__default__', name: 'Algemeen', label: { nl: 'Algemeen', en: 'General' }, order: 0 };

  ungroupedAttributes = computed(() => {
    const attrs = this.editableAttributes();
    const groupIds = new Set((this.entityType()?.groups || []).map(g => g.id));
    return attrs.filter(a => !a.group || !groupIds.has(a.group));
  });

  resolvedTabs = computed((): AttributeTab[] => {
    const et = this.entityType();
    const definedTabs = et?.tabs?.length ? [...et.tabs].sort((a, b) => a.order - b.order) : [];
    const hasUngroupedAttrs = this.ungroupedAttributes().length > 0;
    const hasUntabbedGroups = (et?.groups || []).some(g => !g.tab);

    if (hasUngroupedAttrs || hasUntabbedGroups || definedTabs.length === 0) {
      return [SparkPoFormComponent.DEFAULT_TAB, ...definedTabs];
    }
    return definedTabs;
  });

  groupsForTab(tab: AttributeTab): AttributeGroup[] {
    const groups = this.entityType()?.groups || [];
    if (tab.id === '__default__') {
      return groups.filter(g => !g.tab).sort((a, b) => a.order - b.order);
    }
    return groups.filter(g => g.tab === tab.id).sort((a, b) => a.order - b.order);
  }

  attrsForGroup(group: AttributeGroup): EntityAttributeDefinition[] {
    return this.editableAttributes().filter(a => a.group === group.id);
  }

  constructor() {
    effect(() => {
      const et = this.entityType();
      const _pid = this.parentId();
      const _ptype = this.parentType();
      if (et) {
        this.loadReferenceOptions();
        this.loadAsDetailTypes();
        this.loadLookupReferenceOptions();
      }
    });
  }

  private toRecord<T>(entries: [string, T][]): Record<string, T> {
    const result: Record<string, T> = {};
    for (const [key, value] of entries) {
      result[key] = value;
    }
    return result;
  }

  async loadReferenceOptions(): Promise<void> {
    const refAttrs = this.optionSourceAttributes().filter(a => a.dataType === 'Reference' && a.query);
    if (refAttrs.length === 0) return;

    const entries = await Promise.all(
      refAttrs.filter(a => a.query).map(async attr => {
        const result = await this.sparkService.executeQueryByName(attr.query!, {
          parentId: this.parentId(),
          parentType: this.parentType(),
        });
        return [attr.name, result.data] as [string, PersistentObject[]];
      })
    );
    const optionsByAttr = this.toRecord(entries);
    this.referenceOptions.set(optionsByAttr);

    // For multi-reference attributes (Reference && isArray), turn each query result
    // into a flat TreeNode list + an in-memory provider for <bs-tree-select>, and a
    // by-id node map used to render chip labels for the current selection.
    const providers: Record<string, InMemoryTreeSelectProvider> = {};
    const nodesByAttr: Record<string, Record<string, TreeNode>> = {};
    for (const attr of refAttrs) {
      if (!attr.isArray) continue;
      const pos = optionsByAttr[attr.name] || [];
      const nodes: TreeNode[] = pos
        .filter(po => !!po.id)
        .map(po => ({ id: po.id!, label: po.breadcrumb || po.name || po.id! }));
      providers[attr.name] = new InMemoryTreeSelectProvider(nodes);
      nodesByAttr[attr.name] = this.toRecord(nodes.map(n => [n.id, n] as [string, TreeNode]));
    }
    this.referenceProviders.set(providers);
    this.referenceNodes.set(nodesByAttr);
  }

  /**
   * Stable TreeNode[] value per multi-reference attribute, derived from the id array
   * in formData and the resolved node map. Recomputes only when formData or the node
   * map changes, so binding it into <bs-tree-select> doesn't churn on every CD pass.
   * Ids without a resolved node fall back to a label of the id itself.
   */
  referenceTreeValues = computed<Record<string, TreeNode[]>>(() => {
    const fd = this.formData();
    const nodesByAttr = this.referenceNodes();
    const result: Record<string, TreeNode[]> = {};
    for (const attr of this.editableAttributes()) {
      if (attr.dataType !== 'Reference' || !attr.isArray) continue;
      const ids: string[] = Array.isArray(fd[attr.name]) ? fd[attr.name] : [];
      const nodeMap = nodesByAttr[attr.name] || {};
      result[attr.name] = ids.map(id => nodeMap[id] ?? { id, label: id });
    }
    return result;
  });

  getReferenceProvider(attr: EntityAttributeDefinition): InMemoryTreeSelectProvider | undefined {
    return this.referenceProviders()[attr.name];
  }

  onReferenceTreeChange(attr: EntityAttributeDefinition, value: TreeNode | TreeNode[] | null): void {
    const nodes = Array.isArray(value) ? value : value ? [value] : [];
    const data = { ...this.formData() };
    data[attr.name] = nodes.map(n => n.id);
    this.formData.set(data);
  }

  async loadAsDetailTypes(): Promise<void> {
    const asDetailAttrs = this.optionSourceAttributes().filter(a => a.dataType === 'AsDetail' && a.asDetailType);
    if (asDetailAttrs.length === 0) return;

    const types = await this.sparkService.getEntityTypes();
    const newAsDetailTypes: Record<string, EntityType> = {};

    for (const attr of asDetailAttrs) {
      const asDetailType = types.find(t => t.clrType === attr.asDetailType);
      if (asDetailType) {
        newAsDetailTypes[attr.name] = asDetailType;

        if (attr.isArray) {
          const perms = await this.sparkService.getPermissions(asDetailType.id);
          this.asDetailPermissions.update(prev => ({ ...prev, [attr.name]: perms }));

          const refCols = asDetailType.attributes.filter(a => a.dataType === 'Reference' && a.query);
          if (refCols.length > 0) {
            const refEntries = await Promise.all(
              refCols.filter(c => c.query).map(async col => {
                const result = await this.sparkService.executeQueryByName(col.query!, {
                  parentId: this.parentId(),
                  parentType: this.parentType(),
                });
                return [col.name, result.data] as [string, PersistentObject[]];
              })
            );
            this.asDetailReferenceOptions.update(prev => ({ ...prev, [attr.name]: this.toRecord(refEntries) }));
          }

          // Inline editing of LookupReference child columns needs their options loaded
          // (keyed by lookup name in the shared lookupReferenceOptions, deduped).
          const lookupCols = asDetailType.attributes.filter(a => a.lookupReferenceType);
          for (const col of lookupCols) {
            const lookupName = col.lookupReferenceType!;
            if (!this.lookupReferenceOptions()[lookupName]) {
              const ref = await this.sparkService.getLookupReference(lookupName);
              this.lookupReferenceOptions.update(prev => ({ ...prev, [lookupName]: ref }));
            }
          }
        }
      }
    }
    this.asDetailTypes.set(newAsDetailTypes);
  }

  async loadLookupReferenceOptions(): Promise<void> {
    const lookupAttrs = this.optionSourceAttributes().filter(a => a.lookupReferenceType);
    if (lookupAttrs.length === 0) return;

    const lookupNames = [...new Set(lookupAttrs.map(a => a.lookupReferenceType!))];
    const entries = await Promise.all(
      lookupNames.map(async name => {
        const ref = await this.sparkService.getLookupReference(name);
        return [name, ref] as [string, LookupReference];
      })
    );
    this.lookupReferenceOptions.set(this.toRecord(entries));
  }

  getLookupOptions(attr: EntityAttributeDefinition): LookupReferenceValue[] {
    const lookupRef = attr.lookupReferenceType ? this.lookupReferenceOptions()[attr.lookupReferenceType] : null;
    return lookupRef?.values.filter(v => v.isActive) || [];
  }

  // Write the value emitted by a top-level spark-reference-picker / spark-lookup-picker
  // back into formData (the same write the old in-form modal selectors performed).
  onReferenceValueChange(attr: EntityAttributeDefinition, id: string | null): void {
    const data = { ...this.formData() };
    data[attr.name] = id;
    this.formData.set(data);
  }

  onLookupValueChange(attr: EntityAttributeDefinition, key: string | null): void {
    const data = { ...this.formData() };
    data[attr.name] = key;
    this.formData.set(data);
  }

  getEditRendererComponent(attr: EntityAttributeDefinition): Type<any> | null {
    if (!attr.renderer) return null;
    const reg = this.rendererRegistry.find(r => r.name === attr.renderer);
    return reg?.editComponent ?? null;
  }

  getEditRendererInputs(component: Type<any>, attr: EntityAttributeDefinition): Record<string, any> {
    return withDeclaredInputs(component, {
      value: this.formData()[attr.name],
      attribute: attr,
      options: attr.rendererOptions,
      valueChange: (newValue: any) => {
        const data = { ...this.formData() };
        data[attr.name] = newValue;
        this.formData.set(data);
      },
    });
  }

  /** Column renderer for a cell of an AsDetail sub-table (so embedded rows honor `col.renderer` too). */
  getAsDetailCellRendererComponent(col: EntityAttributeDefinition): Type<any> | null {
    if (!col.renderer) return null;
    return this.rendererRegistry.find(r => r.name === col.renderer)?.columnComponent ?? null;
  }

  getAsDetailCellRendererInputs(component: Type<any>, row: Record<string, any>, col: EntityAttributeDefinition): Record<string, any> {
    return withDeclaredInputs(component, {
      value: row[col.name],
      attribute: col,
      options: col.rendererOptions,
      item: row,
    });
  }

  /** Edit-renderer for an inline AsDetail cell (so inline editing honors `col.renderer`, not just display). */
  getAsDetailCellEditRenderer(col: EntityAttributeDefinition): Type<any> | null {
    if (!col.renderer) return null;
    return this.rendererRegistry.find(r => r.name === col.renderer)?.editComponent ?? null;
  }

  getAsDetailCellEditRendererInputs(component: Type<any>, row: Record<string, any>, col: EntityAttributeDefinition): Record<string, any> {
    return withDeclaredInputs(component, {
      value: row[col.name],
      attribute: col,
      options: col.rendererOptions,
      item: row,
      valueChange: (newValue: any) => {
        row[col.name] = newValue;
        this.onFieldChange();
      },
    });
  }

  /**
   * Rules evaluated in the browser, against the *effective* metadata — so a rule a refresh hook
   * imposed is visible before the round-trip rather than only after the server rejects the save.
   */
  clientRuleFailures = computed<RuleFailure[]>(() => {
    const values = this.formData();
    return this.editableAttributes().flatMap(attr => evaluateRules(attr, values[attr.name]));
  });

  hasError(attrName: string): boolean {
    return this.clientRuleFailures().some(f => f.attributeName === attrName)
      || this.validationErrors().some(e => e.attributeName === attrName);
  }

  // Per-cell validation for inline AsDetail rows. Server-emitted errors are keyed by the
  // path "{attr}[{rowIndex}].{col}"; the client surfaces them like top-level field errors.
  private inlineErrorPath(attr: EntityAttributeDefinition, rowIndex: number, col: EntityAttributeDefinition): string {
    return `${attr.name}[${rowIndex}].${col.name}`;
  }

  hasInlineError(attr: EntityAttributeDefinition, rowIndex: number, col: EntityAttributeDefinition): boolean {
    const path = this.inlineErrorPath(attr, rowIndex, col);
    return this.validationErrors().some(e => e.attributeName === path);
  }

  inlineErrorMessage(attr: EntityAttributeDefinition, rowIndex: number, col: EntityAttributeDefinition): string | null {
    const path = this.inlineErrorPath(attr, rowIndex, col);
    const error = this.validationErrors().find(e => e.attributeName === path);
    return error ? resolveTranslation(error.errorMessage) : null;
  }

  /**
   * The single funnel every scalar / boolean / inline-cell edit passes through.
   *
   * `attr` is optional only so the AsDetail modal's recursive form, which has no trigger context,
   * can still call it. A caller that knows which attribute changed should always say so — without it
   * no refresh can fire.
   */
  onFieldChange(attr?: EntityAttributeDefinition): void {
    this.formData.set({ ...this.formData() });
    if (attr) this.noteChange(attr);
  }

  /**
   * A trigger inside an AsDetail row. Addressed by the same `{attr}[{index}].{col}` path the inline
   * validation errors already use, so the server can tell which row asked without a second
   * addressing scheme being invented for it.
   */
  onInlineCellChange(attr: EntityAttributeDefinition, rowIndex: number, col: EntityAttributeDefinition): void {
    this.onFieldChange();
    if (col.triggersRefresh !== true || !this.objectTypeId()) return;

    const path = this.inlineErrorPath(attr, rowIndex, col);
    if (triggersImmediately(col)) {
      void this.refreshCoordinator.trigger(path);
    } else {
      this.refreshCoordinator.markPending(path);
    }
  }

  onInlineCellBlur(attr: EntityAttributeDefinition, rowIndex: number, col: EntityAttributeDefinition): void {
    if (col.triggersRefresh !== true || !this.objectTypeId()) return;
    void this.refreshCoordinator.blur(this.inlineErrorPath(attr, rowIndex, col));
  }

  /** Blur handler for free-text editors — sends the refresh their keystrokes only marked pending. */
  onFieldBlur(attr: EntityAttributeDefinition): void {
    if (!this.canRefresh(attr)) return;
    void this.refreshCoordinator.blur(attr.name);
  }

  private noteChange(attr: EntityAttributeDefinition): void {
    if (!this.canRefresh(attr)) return;

    if (triggersImmediately(attr)) {
      void this.refreshCoordinator.trigger(attr.name);
    } else {
      // Free text: marking is all a keystroke earns. The request goes on blur, or on save.
      this.refreshCoordinator.markPending(attr.name);
    }
  }

  private canRefresh(attr: EntityAttributeDefinition): boolean {
    return attr.triggersRefresh === true && !!this.objectTypeId();
  }

  /**
   * Per-instance, never a service: the retry-action modal renders its own `spark-po-form`, and a
   * refresh may carry a retry operation — so a refresh can open a modal containing a form that
   * refreshes. A shared coordinator would let the nested form supersede this one's request.
   */
  protected readonly refreshCoordinator = new RefreshCoordinator({
    send: (triggeredBy) => this.sparkService.refresh(
      this.objectTypeId()!, this.buildRefreshPayload(), triggeredBy),
    currentValues: () => this.formData(),
    apply: (response, sent) => {
      this.refreshOverlay.set(overlayFromResponse(response));
      this.formData.set(mergeRefreshValues(sent, this.formData(), response));
      this.applyRefreshedOptions(response);
    },
    setBusy: (busy) => this.isRefreshing.set(busy),
  });

  private buildRefreshPayload() {
    const et = this.entityType();
    const values = this.formData();
    return {
      id: this.objectId(),
      name: et?.name ?? '',
      // The EntityType's id, never `objectTypeId()` — that is the ROUTE segment, which is an alias
      // ("car") as often as a guid. The server types this field as a Guid, so an alias fails
      // deserialization and the request 500s before the handler is reached: no hook, no error the
      // client can act on. The route segment still carries the alias, which the server resolves.
      objectTypeId: et?.id ?? this.objectTypeId()!,
      attributes: (et?.attributes ?? []).map(a => ({
        name: a.name,
        value: values[a.name] ?? null,
        isValueChanged: true,
      })),
    } as any;
  }

  /**
   * Folds replaced option lists into the signals the editors already read, so a refreshed dropdown
   * renders through the same path as a loaded one.
   *
   * `undefined` means the hook did not touch this attribute's options and the loaded set stands; an
   * empty array means it deliberately left none. Collapsing the two would blank every dropdown the
   * hook never mentioned.
   */
  private applyRefreshedOptions(response: PersistentObject): void {
    const replaced = (response.attributes ?? [])
      .map(a => [a.name, (a as { options?: RefreshedOption[] | null }).options] as const)
      .filter(([, options]) => options !== undefined && options !== null);

    if (replaced.length === 0) return;

    const byName = new Map(this.optionSourceAttributes().map(a => [a.name, a]));

    this.lookupReferenceOptions.update(prev => {
      const next = { ...prev };
      for (const [name, options] of replaced) {
        const attr = byName.get(name);
        if (!attr?.lookupReferenceType) continue;
        next[attr.lookupReferenceType] = {
          ...(next[attr.lookupReferenceType] ?? { name: attr.lookupReferenceType, isTransient: true, displayType: ELookupDisplayType.Dropdown }),
          values: (options ?? []).map(o => ({ key: o.key, values: o.label ?? { en: o.key }, isActive: true })),
        } as LookupReference;
      }
      return next;
    });

    this.referenceOptions.update(prev => {
      const next = { ...prev };
      for (const [name, options] of replaced) {
        const attr = byName.get(name);
        if (attr?.dataType !== 'Reference') continue;
        next[name] = (options ?? []).map(o => ({
          id: o.key,
          name: attr.referenceType ?? '',
          objectTypeId: '',
          breadcrumb: o.label ? resolveTranslation(o.label) : o.key,
          attributes: [],
        })) as PersistentObject[];
      }
      return next;
    });
  }

  /** Sends anything still pending, so a typed-but-never-blurred trigger is reflected before save. */
  async flushPendingRefresh(): Promise<void> {
    await this.refreshCoordinator.flush();
  }

  async onSave(): Promise<void> {
    // A value typed and never blurred — the user tabbing straight to Save — must still reshape the
    // object before it goes, or the server enforces rules the user was never shown.
    await this.flushPendingRefresh();

    // Evaluated AFTER the flush: the refresh may be what imposes the rule being checked.
    if (this.clientRuleFailures().length > 0) return;

    this.save.emit();
  }

  onCancel(): void {
    this.cancel.emit();
  }

  // AsDetail object modal methods
  openAsDetailEditor(attr: EntityAttributeDefinition): void {
    this.editingAsDetailAttr.set(attr);
    this.editingArrayIndex.set(null);
    this.asDetailFormData.set({ ...(this.formData()[attr.name] || {}) });
    this.showAsDetailModal.set(true);
  }

  saveAsDetailObject(): void {
    const attr = this.editingAsDetailAttr();
    if (attr) {
      const data = { ...this.formData() };
      if (attr.isArray) {
        const arr = [...(data[attr.name] || [])];
        const idx = this.editingArrayIndex();
        if (idx !== null) {
          arr[idx] = { ...this.asDetailFormData() };
        } else {
          arr.push({ ...this.asDetailFormData() });
        }
        data[attr.name] = arr;
      } else {
        data[attr.name] = { ...this.asDetailFormData() };
      }
      this.formData.set(data);
    }
    this.closeAsDetailModal();
  }

  closeAsDetailModal(): void {
    this.showAsDetailModal.set(false);
    this.editingAsDetailAttr.set(null);
    this.editingArrayIndex.set(null);
    this.asDetailFormData.set({});
  }

  // Inline AsDetail methods
  addInlineRow(attr: EntityAttributeDefinition): void {
    const data = { ...this.formData() };
    const arr = [...(data[attr.name] || [])];
    arr.push({});
    data[attr.name] = arr;
    this.formData.set(data);
  }

  // Array AsDetail methods
  addArrayItem(attr: EntityAttributeDefinition): void {
    this.editingAsDetailAttr.set(attr);
    this.editingArrayIndex.set(null);
    this.asDetailFormData.set({});
    this.showAsDetailModal.set(true);
  }

  editArrayItem(attr: EntityAttributeDefinition, index: number): void {
    this.editingAsDetailAttr.set(attr);
    this.editingArrayIndex.set(index);
    const arr = this.formData()[attr.name] || [];
    this.asDetailFormData.set({ ...(arr[index] || {}) });
    this.showAsDetailModal.set(true);
  }

  removeArrayItem(attr: EntityAttributeDefinition, index: number): void {
    const data = { ...this.formData() };
    const arr = [...(data[attr.name] || [])];
    arr.splice(index, 1);
    data[attr.name] = arr;
    this.formData.set(data);
  }

  // Drag-reorder for [Sortable] AsDetail arrays. Order = array position, so moving the
  // row within formData()[attr.name] and re-emitting the signal IS the persisted order
  // (po-edit flags AsDetail attributes isValueChanged on save).
  onAsDetailReorder(attr: EntityAttributeDefinition, event: CdkDragDrop<Record<string, any>[]>): void {
    if (event.previousIndex === event.currentIndex) return;
    const data = { ...this.formData() };
    const arr = [...(data[attr.name] ?? [])];
    moveItemInArray(arr, event.previousIndex, event.currentIndex);
    data[attr.name] = arr;
    this.formData.set(data);
  }
}
