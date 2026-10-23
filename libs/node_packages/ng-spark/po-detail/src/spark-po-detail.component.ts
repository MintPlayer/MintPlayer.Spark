import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, output, signal, TemplateRef, Type, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule, Location, NgTemplateOutlet, NgComponentOutlet } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { sparkSlug } from './spark-slug';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsContainerComponent } from '@mintplayer/ng-bootstrap/container';
import { BsGridComponent, BsGridRowDirective, BsGridColumnDirective } from '@mintplayer/ng-bootstrap/grid';
import { BsPriorityNavComponent, BsPriorityNavItemDirective } from '@mintplayer/ng-bootstrap/priority-nav';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { BsTabControlComponent, BsTabPageComponent, BsTabPageHeaderDirective } from '@mintplayer/ng-bootstrap/tab-control';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { SparkService, SparkLanguageService, SparkLiveService, SparkReturnNavigationService, describeSparkError } from '@mintplayer/ng-spark/services';
import { SparkAttributePatch, SparkAttributeRefreshService, SparkClientMethodRegistry, SparkQueryRefreshService } from '@mintplayer/ng-spark/client-operations';
import {
  TranslateKeyPipe,
  ResolveTranslationPipe,
  AttributeValuePipe,
  OrDashPipe,
  RawAttributeValuePipe,
  AsDetailColumnsPipe,
  AsDetailCellValuePipe,
  ArrayValuePipe,
  ReferenceLinkRoutePipe,
  ReferenceChipsPipe,
  ParsedDatePipe,
  AsDetailRowRendererPipe,
  AsDetailLeadingRendererPipe,
} from '@mintplayer/ng-spark/pipes';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';
import { SparkAttributeDescriptionComponent } from '@mintplayer/ng-spark/attribute-description';
import { SparkQueryCardComponent, SparkGridCellComponent } from '@mintplayer/ng-spark/grid';
import { SPARK_ATTRIBUTE_RENDERERS, SparkResolvedRowRenderer, rendererValue, rowRendererInputs, withDeclaredInputs } from '@mintplayer/ng-spark/renderers';
import {
  AttributeGroup,
  AttributeTab,
  CustomActionDefinition,
  EntityAttributeDefinition,
  EntityType,
  LookupReference,
  PersistentObject,
  QueryResultItem,
  ShowedOn,
  hasShowedOnFlag,
  isPrimitiveArrayAttribute,
  EntityPermissions,
  SparkDeletedFilter,
  builtInAction,
  confirmationText,
  filterDetailActions,
  isShowedOnDetail,
  subQueriesOf,
} from '@mintplayer/ng-spark/models';
import {
  SPARK_DETAIL_ACTIONS,
  SPARK_DETAIL_LAYOUTS,
  SPARK_DETAIL_PANELS,
  SparkDetailContext,
  orderSparkExtensions,
  parseSparkDeletedParam,
} from '@mintplayer/ng-spark/panels';
import { Subscription, combineLatest } from 'rxjs';

@Component({
  selector: 'spark-po-detail',
  imports: [CommonModule, NgTemplateOutlet, NgComponentOutlet, RouterModule, BsAlertComponent, BsBadgeComponent,BsCardComponent, BsCardHeaderComponent, BsContainerComponent, BsGridComponent, BsGridRowDirective, BsGridColumnDirective, BsPriorityNavComponent, BsPriorityNavItemDirective, BsTableComponent, BsTabControlComponent, BsTabPageComponent, BsTabPageHeaderDirective, BsSpinnerComponent, SparkIconComponent, SparkQueryCardComponent, SparkGridCellComponent, ResolveTranslationPipe, TranslateKeyPipe, AttributeValuePipe, OrDashPipe, RawAttributeValuePipe, AsDetailColumnsPipe, AsDetailRowRendererPipe, AsDetailLeadingRendererPipe, AsDetailCellValuePipe, ArrayValuePipe, ReferenceLinkRoutePipe, ReferenceChipsPipe, ParsedDatePipe, SparkAttributeDescriptionComponent],
  templateUrl: './spark-po-detail.component.html',
  styleUrl: './spark-po-detail.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class SparkPoDetailComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly location = inject(Location);
  private readonly sparkService = inject(SparkService);
  private readonly returnNavigation = inject(SparkReturnNavigationService);
  private readonly queryRefresh = inject(SparkQueryRefreshService);
  private readonly attributeRefresh = inject(SparkAttributeRefreshService);
  private readonly live = inject(SparkLiveService);
  protected readonly lang = inject(SparkLanguageService);
  private readonly clientMethods = inject(SparkClientMethodRegistry);
  protected readonly rendererRegistry = inject(SPARK_ATTRIBUTE_RENDERERS);

  /**
   * Add-on panels and action-bar buttons (#460, `SPARK_DETAIL_PANELS` / `SPARK_DETAIL_ACTIONS`).
   * Multi-provided, so History, SoftDelete and Moderation can each add theirs to the page
   * `sparkRoutes()` routes to, which is created by the router and takes no templates.
   */
  protected readonly detailPanels = orderSparkExtensions(inject(SPARK_DETAIL_PANELS, { optional: true }), p => p.order);
  protected readonly detailActions = orderSparkExtensions(inject(SPARK_DETAIL_ACTIONS, { optional: true }), a => a.priority ?? 60);
  private readonly detailLayouts = orderSparkExtensions(inject(SPARK_DETAIL_LAYOUTS, { optional: true }), () => undefined);

  /**
   * The object to show (type id or alias, and id), for a host that embeds this component — a board
   * tile (#530). Set, they replace the route's `:type`/`:id`, and the URL's slug is left alone.
   */
  objectType = input<string | null>(null);
  objectId = input<string | null>(null);
  /** The sub-query cards below the attributes. */
  showQueries = input(true);
  /** The action bar, the title and the add-on panels: off where the host draws its own (a tile). */
  showChrome = input(true);

  showCustomActions = input(true);
  extraActionsTemplate = input<TemplateRef<void> | null>(null);
  extraContentTemplate = input<TemplateRef<{ $implicit: PersistentObject; entityType: EntityType }> | null>(null);

  /**
   * Content rendered after the attribute cards and BEFORE the first sub-query card, with the same
   * `{ $implicit: PersistentObject; entityType: EntityType }` context as `extraContentTemplate`.
   *
   * `extraContentTemplate` renders last, below every query card, which is the wrong place for
   * something the reader must see first (a notice, a call to action): on a page with a long
   * sub-query grid it lands below the fold.
   */
  beforeQueriesTemplate = input<TemplateRef<{ $implicit: PersistentObject; entityType: EntityType }> | null>(null);

  /**
   * Header slots forwarded to every sub-query card on this page.
   *
   * A structural directive cannot cross a component boundary, and this component is created by
   * the router — in a default app there is no `<spark-po-detail>` tag to project content into.
   * So an app that wants slotted chrome on its sub-queries substitutes its own route component
   * via `SparkRouteConfig.poDetail` and forwards the templates through here. Forwarding a
   * `TemplateRef` as an input is already this component's idiom; see the two above.
   *
   * Each slot may target one query by alias, so a single forwarded set serves a page rendering
   * several sub-queries.
   */
  queryIconTemplate = input<TemplateRef<any> | null>(null);
  queryCaptionTemplate = input<TemplateRef<any> | null>(null);
  queryActionsTemplate = input<TemplateRef<any> | null>(null);

  edited = output<void>();
  deleted = output<void>();
  /** After an action succeeded; `result` is what it handed to `CustomActionArgs.SetResult` (a download url, a new id). */
  customActionExecuted = output<{ action: CustomActionDefinition; item: PersistentObject; result?: unknown }>();

  colors = Color;
  /** The page could not be loaded: shown instead of the page. */
  errorMessage = signal<string | null>(null);
  /** A custom action failed or was refused: shown above the page, which stays (it used to replace the page). */
  actionError = signal<string | null>(null);
  entityType = signal<EntityType | null>(null);

  /** The sub-queries rendered as cards below the form, in object form (#460, D17). */
  protected readonly subQueries = computed(() => subQueriesOf(this.entityType()));
  allEntityTypes = signal<EntityType[]>([]);
  item = signal<PersistentObject | null>(null);
  lookupReferenceOptions = signal<Record<string, LookupReference>>({});
  asDetailTypes = signal<Record<string, EntityType>>({});
  asDetailReferenceOptions = signal<Record<string, Record<string, QueryResultItem[]>>>({});
  type = '';
  id = '';
  /** The catalogue's Edit (#467, D8) when this page offers it: listed for the caller, on the detail side, and allowed on this row. */
  editAction = signal<CustomActionDefinition | null>(null);
  /** The catalogue's Delete, on the same terms as {@link editAction}. */
  deleteAction = signal<CustomActionDefinition | null>(null);
  canEdit = computed(() => this.editAction() !== null);
  canDelete = computed(() => this.deleteAction() !== null);
  customActions = signal<CustomActionDefinition[]>([]);
  /** Type-level rights, kept for the add-on context; null until loaded. */
  permissions = signal<EntityPermissions | null>(null);

  /**
   * The route's `?deleted=` mode (#460, T2): `only` when the row was opened from the recycle bin.
   * Sent with every load of this object; the server honours it only for `ViewDeleted` holders, so
   * for anyone else a deleted row stays a 404.
   */
  deletedMode = signal<SparkDeletedFilter | null>(null);

  /** True when the page shows a soft-deleted row: nothing but Restore/Purge applies to it. */
  protected readonly isDeletedView = computed(() => this.deletedMode() === 'only');

  /** What every add-on panel and action receives; rebuilt whenever the object or rights change. */
  protected readonly detailContext = computed((): SparkDetailContext | null => {
    const item = this.item();
    const entityType = this.entityType();
    if (!item || !entityType) return null;
    return {
      type: this.type,
      id: this.id,
      item,
      entityType,
      permissions: this.permissions(),
      deleted: this.deletedMode(),
      reload: () => this.load(),
    };
  });

  /** The registered layout that draws this object's attributes instead of the tabs (#504), if one matches. */
  protected readonly detailLayout = computed(() => {
    const item = this.item();
    const entityType = this.entityType();
    if (!item || !entityType) return null;
    return this.detailLayouts.find(l => l.matches(item, entityType)) ?? null;
  });

  /**
   * The actions actually offered for the object on screen.
   *
   * The catalogue at `/spark/actions/{objectTypeId}` is per TYPE -- the server is never told which
   * row is open -- so an action that applies to only some rows cannot be filtered there. The
   * entity's actions hook decides while it has the entity in hand and withholds what does not
   * apply, and the object arrives carrying that answer.
   *
   * Since #460 (D13) the server asks the same hook again when the action is submitted and refuses a
   * disabled one with 403, so hiding it here matches what the server will do. What this prevents is
   * offering a destructive action where it cannot possibly apply -- Coverage showed an irreversible
   * red "Delete data" button on every repository page, healthy ones included, and only admitted it
   * would refuse after the confirmation prompt.
   */
  visibleCustomActions = computed(() => {
    const withheld = this.item()?.disabledActions;
    if (!withheld?.length) return this.customActions();

    const lowered = new Set(withheld.map(name => name.toLowerCase()));
    return this.customActions().filter(action => !lowered.has(action.name.toLowerCase()));
  });

  constructor() {
    // The query map too: Restore navigates from `?deleted=only` to the same path without it, and the
    // object must then be re-read as a live row. The first emission is synchronous, before the inputs
    // are set, so an embedded object is told apart by the route having no `:type` (a board's route).
    combineLatest([this.route.paramMap, this.route.queryParamMap])
      .pipe(takeUntilDestroyed())
      .subscribe(([params, query]) => {
        const type = params.get('type');
        if (!type || this.objectType()) return;
        this.onTargetChange(type, params.get('id') || '', query.get('deleted'));
      });

    // An embedded object: follows the inputs, and a change of them loads the other object.
    effect(() => {
      const type = this.objectType();
      const id = this.objectId() ?? '';
      if (type) untracked(() => this.onTargetChange(type, id, null));
    });

    // Server-issued `refreshAttribute` patches for the object this page is showing.
    //
    // Tracks the token and the entity type, never `item()`: writing the signal this effect reads
    // would re-run it forever, so the read-modify-write of the object happens untracked.
    effect(() => {
      const objectTypeId = this.entityType()?.id;
      if (!this.attributeRefresh.tokenFor(objectTypeId, this.id)) return;

      const patches = this.attributeRefresh.patchesFor(objectTypeId, this.id);
      untracked(() => this.applyAttributePatches(patches));
    });

    inject(DestroyRef).onDestroy(() => this.stopFollowing());
  }

  // --- live (X4) ---------------------------------------------------------------------------------

  /** The live subscription of the object on screen, while its type is live; keyed so a reload keeps it. */
  private liveSubscription: Subscription | null = null;
  private liveKey: string | null = null;
  /** The catalogue and rights of the last load, so a pushed object re-derives its Edit/Delete/actions. */
  private loadedActions: CustomActionDefinition[] | null = null;

  /**
   * Follows the object when its type is live: every push replaces the shown object and re-derives Edit,
   * Delete and the custom actions from its own `can`/`disabledActions`, as a load does. `gone` (deleted, or
   * no longer readable — one answer) shows the unavailable state. A subscription `error` stops following
   * and keeps the loaded object: the page still shows what the HTTP load returned.
   */
  private follow(et: EntityType | null): void {
    if (!et?.live || !this.id) {
      this.stopFollowing();
      return;
    }
    const deleted = this.deletedMode();
    const key = JSON.stringify([et.id, this.id, deleted]);
    if (key === this.liveKey && this.liveSubscription) return;
    this.stopFollowing();
    this.liveKey = key;
    this.liveSubscription = this.live.watchPersistentObject(et.id, this.id, { deleted }).subscribe(event => {
      if (event.type === 'po') {
        this.item.set(event.po);
        this.deriveActions(event.po);
      } else if (event.type === 'gone') {
        this.liveKey = null;
        this.liveSubscription = null;
        this.item.set(null);
        this.errorMessage.set(this.translateOr('spark.po.unavailable', 'This item was deleted or is no longer available.'));
      } else {
        this.liveKey = null;
        this.liveSubscription = null;
        console.warn(`[spark] live updates stopped for ${et.name} ${this.id}: ${event.code}: ${event.message}`);
      }
    });
  }

  private stopFollowing(): void {
    this.liveSubscription?.unsubscribe();
    this.liveSubscription = null;
    this.liveKey = null;
  }

  /** The translation, or `fallback` while the key has none (`t` echoes a missing key). */
  private translateOr(key: string, fallback: string): string {
    const text = this.lang.t(key);
    return text && text !== key ? text : fallback;
  }

  /**
   * Applies patched attribute values in place, without a re-fetch.
   *
   * The operation carries the value the server computed after its own write, so this is both
   * cheaper and more correct than re-reading: a re-read goes through a RavenDB index that may
   * still be stale from the very write that produced the patch, and would show the old value.
   */
  private applyAttributePatches(patches: Record<string, SparkAttributePatch>): void {
    const current = this.item();
    if (!current) return;

    let changed = false;
    const attributes = current.attributes.map(attribute => {
      if (!Object.prototype.hasOwnProperty.call(patches, attribute.name)) return attribute;

      const patch = patches[attribute.name];
      const next = { ...attribute };
      let patched = false;

      // Each field is applied on its own, because they are not interchangeable: an AsDetail
      // attribute carries its rows in object/objects and leaves value null, so applying only
      // value could never refresh a detail grid.
      //
      // Compared by identity in every case. A scalar skips when unchanged, and a null skips
      // against a null -- which matters because the server writes object/objects as null on a
      // scalar patch, and treating that as a change would repaint on every patch. A rewritten
      // collection is a new array and so never matches, which is the behaviour wanted: comparing
      // rows deeply would cost more than the repaint it saves.
      if (patch.value !== undefined && next.value !== patch.value) {
        next.value = patch.value;
        patched = true;
      }
      // Only for the attributes that actually use them. The server writes object/objects as
      // null on every scalar patch, so applying them unconditionally would mark a scalar
      // attribute changed on each patch and repaint for nothing.
      if (attribute.dataType === 'AsDetail') {
        if (patch.object !== undefined && next.object !== patch.object) {
          next.object = patch.object as PersistentObject | null;
          patched = true;
        }
        if (patch.objects !== undefined && next.objects !== patch.objects) {
          next.objects = patch.objects as PersistentObject[] | null;
          patched = true;
        }
      }

      if (!patched) return attribute;
      changed = true;
      return next;
    });

    if (changed) this.item.set({ ...current, attributes });
  }

  private async onTargetChange(type: string, id: string, deleted: string | null): Promise<void> {
    this.type = type;
    this.id = id;
    // The previous object's pushes must not land on this one, even when this load fails.
    this.stopFollowing();
    const mode = parseSparkDeletedParam(deleted);
    // `exclude` is the default on the wire; keep it out of the request like an absent parameter.
    this.deletedMode.set(mode === 'exclude' ? null : mode);
    await this.load();
  }

  /**
   * With `sparkRoutes({ slugs: true })`, puts the breadcrumb's slug in the URL, or corrects a stale one, without a
   * navigation. A slug of "edit" is left out: that URL is the edit page.
   */
  private writeSlug(item: PersistentObject): void {
    if (this.objectType() || !this.route.snapshot.data['sparkSlug']) return;
    const slug = sparkSlug(item.breadcrumb);
    if (slug === (this.route.snapshot.paramMap.get('slug') ?? '')) return;
    const commands = slug && slug !== 'edit' ? ['/po', this.type, this.id, slug] : ['/po', this.type, this.id];
    const tree = this.router.createUrlTree(commands, { queryParams: this.route.snapshot.queryParams });
    this.location.replaceState(this.router.serializeUrl(tree));
  }

  /** The object as the page's current mode sees it. Only a set mode is sent. */
  private loadItem(): Promise<PersistentObject> {
    const deleted = this.deletedMode();
    return deleted
      ? this.sparkService.get(this.type, this.id, { deleted })
      : this.sparkService.get(this.type, this.id);
  }

  /** (Re)loads the object, its type, rights and actions. Also what add-ons call via `context.reload()`. */
  async load(): Promise<void> {
    this.errorMessage.set(null);
    // A load overtaken by a move to another object publishes nothing.
    const type = this.type, id = this.id;
    const stale = () => type !== this.type || id !== this.id;
    try {
      const [entityTypes, item] = await Promise.all([
        this.sparkService.getEntityTypes(),
        this.loadItem()
      ]);

      const et = entityTypes.find(t => t.id === this.type || t.alias === this.type) || null;
      // Rights and actions are fetched BEFORE the object is published. The component is reused when
      // the route moves to another object, so publishing the new object first would show it under
      // the previous object's Edit/Delete/custom actions until these arrive: a Delete the caller
      // holds on the page they came from flashed on one where they hold none.
      const [permissions, actions] = et
        ? await Promise.all([
          this.sparkService.getPermissions(et.id),
          this.sparkService.getCustomActions(et.id)
        ])
        : [null, null];
      if (stale()) return;

      this.allEntityTypes.set(entityTypes);
      this.entityType.set(et);
      this.item.set(item);
      this.writeSlug(item);
      this.loadLookupReferenceOptions();
      this.loadAsDetailTypes();

      this.permissions.set(et && permissions && actions ? permissions : null);
      this.loadedActions = et && permissions && actions ? actions : null;
      this.deriveActions(item);
      this.follow(et);
    } catch (e) {
      if (stale()) return;
      const error = e as HttpErrorResponse;
      this.errorMessage.set(error.error?.error || error.message || 'An unexpected error occurred');
    }
  }

  /**
   * Edit, Delete and the custom actions for `item`, from the last load's catalogue and rights: on a load,
   * and again on every live push, since `can` and `disabledActions` travel with the object.
   */
  private deriveActions(item: PersistentObject): void {
    const permissions = this.permissions();
    const actions = this.loadedActions;
    if (!permissions || !actions) {
      this.editAction.set(null);
      this.deleteAction.set(null);
      this.customActions.set([]);
    } else {
      // Prefer the per-row affordances (#236 G5) when the server attached them — a row the
      // caller may read but not mutate then hides its Edit/Delete buttons instead of rendering
      // ones that 404. `can` is absent for types with no row rule, so fall back to type-level.
      // The override (not an intersection) is safe because the server computes `can` as the
      // intersection of type-level rights and the row rule (#243) — it never claims more than
      // `permissions` does.
      const can = item.can;
      // The built-in actions honour `disabledActions` too (#460, D13): the server's
      // OnDisableActionsAsync decides, and refuses a disabled Edit/Save/Delete with 403 at submit,
      // so offering the button would only lead to that refusal. Case-insensitive, like the
      // custom-action filter below.
      const withheld = new Set((item.disabledActions ?? []).map(name => name.toLowerCase()));
      // A row opened from the recycle bin (#460): Edit and Delete judge live rows only — the
      // SoftDelete policy hides a deleted row from both — so they would 404. Restore/Purge come
      // from the soft-delete entry point's detail action instead.
      const deletedView = this.isDeletedView();
      // Edit and Delete come from the composed catalogue (#467, D8): listed only when the caller
      // holds Edit/T or Delete/T, shown here when their showedOn includes the detail side (an app
      // removes one with "Edit": null), and the row's `can` must allow it too. The selection rule
      // does not apply: the page acts on the one object it shows.
      const edit = builtInAction(actions, 'Edit');
      const del = builtInAction(actions, 'Delete');
      this.editAction.set(edit && isShowedOnDetail(edit.showedOn) && !deletedView
        && (can ? can.edit : permissions.canEdit) && !withheld.has('edit') && !withheld.has('save') ? edit : null);
      this.deleteAction.set(del && isShowedOnDetail(del.showedOn) && !deletedView
        && (can ? can.delete : permissions.canDelete) && !withheld.has('delete') ? del : null);
      // Custom actions only: the built-ins are rendered above as this page's Edit and Delete.
      this.customActions.set(deletedView ? [] : filterDetailActions(actions));
    }
  }

  /**
   * Drawn from the loaded object's own `showedOn`, falling back to the model's (#264, G-Q3): an action sets
   * it per object in `OnLoadAsync` — a stolen car's police report number is shown, the model says `None`.
   */
  visibleAttributes = computed(() => {
    const loaded = new Map((this.item()?.attributes ?? []).map(a => [a.name, a] as const));
    return this.entityType()?.attributes
      .filter(a => hasShowedOnFlag(loaded.get(a.name)?.showedOn ?? a.showedOn, ShowedOn.PersistentObject))
      .sort((a, b) => a.order - b.order) || [];
  });

  private static readonly DEFAULT_TAB: AttributeTab = { id: '__default__', name: 'Algemeen', label: { nl: 'Algemeen', en: 'General' }, order: 0 };

  ungroupedAttributes = computed(() => {
    const attrs = this.visibleAttributes();
    const groupIds = new Set((this.entityType()?.groups || []).map(g => g.id));
    return attrs.filter(a => !a.group || !groupIds.has(a.group));
  });

  resolvedTabs = computed((): AttributeTab[] => {
    const et = this.entityType();
    const definedTabs = et?.tabs?.length ? [...et.tabs].sort((a, b) => a.order - b.order) : [];
    const hasUngroupedAttrs = this.ungroupedAttributes().length > 0;
    const hasUntabbedGroups = (et?.groups || []).some(g => !g.tab);

    if (hasUngroupedAttrs || hasUntabbedGroups || definedTabs.length === 0) {
      return [SparkPoDetailComponent.DEFAULT_TAB, ...definedTabs];
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
    return this.visibleAttributes().filter(a => a.group === group.id);
  }

  /** A list of plain values (`List<string>`, `int[]`): one chip per item. */
  isPrimitiveList(attr: EntityAttributeDefinition): boolean {
    return isPrimitiveArrayAttribute(attr);
  }

  /** The non-blank items of a plain-value list, as text; empty for a missing list. */
  primitiveListItems(value: unknown): string[] {
    if (!Array.isArray(value)) return [];
    return value.filter(v => v !== null && v !== undefined && String(v).trim() !== '').map(v => String(v));
  }

  /** Whether the attribute's renderer asks for the whole row, without a label (`fullWidth`). */
  isFullWidthRenderer(attr: EntityAttributeDefinition): boolean {
    if (!attr.renderer) return false;
    return this.rendererRegistry.find(r => r.name === attr.renderer)?.fullWidth === true;
  }

  getDetailRendererComponent(attr: EntityAttributeDefinition): Type<any> | null {
    if (!attr.renderer) return null;
    return this.rendererRegistry.find(r => r.name === attr.renderer)?.detailComponent ?? null;
  }

  /** Inputs of an AsDetail row or leading-cell renderer: the row and the object that owns it. */
  getRowRendererInputs(renderer: SparkResolvedRowRenderer, row: Record<string, any>, item: PersistentObject): Record<string, any> {
    return rowRendererInputs(renderer, row, item?.id, item);
  }

  getDetailRendererInputs(component: Type<any>, attr: EntityAttributeDefinition, item: PersistentObject): Record<string, any> {
    const itemAttr = item.attributes.find(a => a.name === attr.name);
    const formData: Record<string, any> = {};
    for (const a of item.attributes) {
      formData[a.name] = rendererValue(a);
    }
    return withDeclaredInputs(component, {
      value: rendererValue(itemAttr),
      attribute: attr,
      options: attr.rendererOptions,
      formData,
      item,
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

  private async loadLookupReferenceOptions(): Promise<void> {
    const lookupAttrs = this.visibleAttributes().filter(a => a.lookupReferenceType);
    if (lookupAttrs.length === 0) return;

    const lookupNames = [...new Set(lookupAttrs.map(a => a.lookupReferenceType!))];
    // A lookup that fails to load only costs its labels: the attribute then shows its raw value
    // (attributeValue's fallback) instead of the whole page erroring over a secondary request (#453).
    const entries = await Promise.all(
      lookupNames.map(async name => {
        const result = await this.sparkService.getLookupReference(name).catch(() => null);
        return [name, result] as const;
      })
    );
    this.lookupReferenceOptions.set(entries.reduce(
      (acc, [k, v]) => v ? { ...acc, [k]: v } : acc,
      {} as Record<string, LookupReference>));
  }

  private async loadAsDetailTypes(): Promise<void> {
    const asDetailAttrs = this.visibleAttributes().filter(a => a.dataType === 'AsDetail' && a.isArray && a.asDetailType);
    if (asDetailAttrs.length === 0) return;

    const types = this.allEntityTypes();
    const newAsDetailTypes: Record<string, EntityType> = {};
    // Same fallback as the form: a row type is usually absent from the Query-gated catalogue, and
    // the server now ships its definition alongside the parent (#385).
    const embedded = this.entityType()?.detailTypes ?? [];

    for (const attr of asDetailAttrs) {
      const asDetailType =
        types.find(t => t.clrType === attr.asDetailType)
        ?? embedded.find(t => t.clrType === attr.asDetailType);
      if (asDetailType) {
        newAsDetailTypes[attr.name] = asDetailType;
        const refCols = asDetailType.attributes.filter(a => a.dataType === 'Reference' && a.query);
        if (refCols.length > 0) {
          const refEntries = await Promise.all(
            refCols.map(async col => {
              const result = await this.sparkService.executeQueryByName(col.query!, {
                parentId: this.id,
                parentType: this.type,
              });
              return [col.name, result.items] as const;
            })
          );
          this.asDetailReferenceOptions.update(prev => ({
            ...prev,
            [attr.name]: refEntries.reduce((acc, [k, v]) => ({ ...acc, [k]: v }), {} as Record<string, QueryResultItem[]>)
          }));
        }
      }
    }
    this.asDetailTypes.set(newAsDetailTypes);
  }

  /**
   * The bootstrap classes for a custom action's button. Outline-primary stays the default, so
   * every existing action looks exactly as it did; a server-declared variant opts one action into
   * a louder style. Restricted to a known set rather than interpolated, because the value arrives
   * from a JSON file and must not be able to put arbitrary classes on the button.
   */
  /** Edit's and Delete's button classes: the catalogue's variant, else the look they always had. */
  protected builtInActionClass(action: CustomActionDefinition, fallback: 'primary' | 'danger'): string {
    const variant = action.variant?.toLowerCase();
    return ['danger', 'warning', 'primary', 'secondary', 'success'].includes(variant ?? '')
      ? `btn btn-${variant}`
      : `btn btn-${fallback}`;
  }

  protected customActionClass(action: CustomActionDefinition): string {
    const variant = action.variant?.toLowerCase();
    switch (variant) {
      case 'danger':
      case 'warning':
      case 'primary':
      case 'secondary':
      case 'success':
        return `btn btn-${variant}`;
      default:
        return 'btn btn-outline-primary';
    }
  }

  /**
   * The action currently running, or null. Drives the disabled state on every custom-action
   * button.
   *
   * A custom action is not necessarily quick: Coverage's Resync makes paged GitHub App calls for
   * every account the caller manages, inside the request. Without this the button stayed live and
   * looked inert for several seconds, and a second click queued a second full run -- which is how
   * a slow action and a broken one became indistinguishable.
   */
  runningAction = signal<string | null>(null);

  /**
   * Why an action cannot run in this browser, translated, for its tooltip: the client method it
   * requires (`requiresClient`) is not registered or not supported here (Q9). Null otherwise.
   */
  protected customActionUnavailableReason(action: CustomActionDefinition): string | null {
    const key = this.clientMethods.unavailableReason(action.requiresClient);
    return key === null ? null : this.lang.t(key);
  }

  async onCustomAction(action: CustomActionDefinition): Promise<void> {
    // Guard re-entry as well as disabling the button: the template is not the only caller, and a
    // host component driving this method directly would otherwise bypass the check.
    if (this.runningAction()) return;
    if (this.clientMethods.unavailableReason(action.requiresClient) !== null) return;

    const message = confirmationText(action, 1);
    if (message && !confirm(message)) return;

    this.runningAction.set(action.name);
    this.actionError.set(null);
    try {
      const result = await this.sparkService.executeCustomAction(this.type, action.name, this.item() || undefined);
      this.customActionExecuted.emit({ action, item: this.item()!, result });
      if (action.refreshOnCompleted) {
        // The sub-query grids below do not depend on item(), so re-fetching the PO left them
        // showing pre-action rows -- the action appeared to have done nothing to the very
        // lists it changed.
        //
        // Before the await, not after (#319): a server-issued refreshQuery was already dispatched
        // inside executeCustomAction, and bumps in one synchronous run coalesce into one grid
        // fetch. Bumping after the re-fetch's network round trip landed in a second
        // change-detection flush, and the grid fetched twice for one click.
        for (const subQuery of this.subQueries()) {
          this.queryRefresh.request(subQuery.query);
        }

        const item = await this.loadItem();
        this.item.set(item);
      }
    } catch (e) {
      // The envelope's reason (and, for a 429, when to try again), never Angular's HTTP status line.
      this.actionError.set(describeSparkError(e, this.lang, (e as HttpErrorResponse)?.message || this.lang.t('common.actionFailed', 'Action failed')));
    } finally {
      // finally, not after the try: an action that fails must not leave every button on the page
      // permanently disabled.
      this.runningAction.set(null);
    }
  }

  onEdit(): void {
    this.edited.emit();
    this.router.navigate(['/po', this.type, this.id, 'edit']);
  }

  async onDelete(): Promise<void> {
    // The version on screen is the one deleted (#467, D14): a row changed since is a 409.
    const etag = this.item()?.etag;
    if (!etag) return;
    const message = confirmationText(this.deleteAction(), 1);
    if (!message || confirm(message)) {
      await this.sparkService.delete(this.type, this.id, etag);
      this.deleted.emit();
      // Back to the list the row was opened from (else the type's list), not the start page.
      await this.returnNavigation.returnToList(this.entityType()?.name);
    }
  }

  onBack(): void {
    window.history.back();
  }
}
