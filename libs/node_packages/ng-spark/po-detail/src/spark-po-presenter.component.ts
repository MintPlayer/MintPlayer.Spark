import { ChangeDetectionStrategy, Component, PendingTasks, Type, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { CommonModule, NgComponentOutlet, NgTemplateOutlet } from '@angular/common';
import { RouterModule } from '@angular/router';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsGridColumnDirective, BsGridComponent, BsGridRowDirective } from '@mintplayer/ng-bootstrap/grid';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { BsTabControlComponent, BsTabPageComponent, BsTabPageHeaderDirective } from '@mintplayer/ng-bootstrap/tab-control';
import { SparkService } from '@mintplayer/ng-spark/services';
import {
  ArrayValuePipe,
  AsDetailCellValuePipe,
  AsDetailColumnsPipe,
  AsDetailLeadingRendererPipe,
  AsDetailRowRendererPipe,
  AttributeValuePipe,
  LinesPipe,
  OrDashPipe,
  ParsedDatePipe,
  RawAttributeValuePipe,
  ReferenceChipsPipe,
  ReferenceLinkRoutePipe,
  ResolveTranslationPipe,
  TranslateKeyPipe,
} from '@mintplayer/ng-spark/pipes';
import { SparkAttributeDescriptionComponent } from '@mintplayer/ng-spark/attribute-description';
import { SparkGridCellComponent } from '@mintplayer/ng-spark/grid';
import { SPARK_ATTRIBUTE_RENDERERS, SparkResolvedRowRenderer, rendererValue, rowRendererInputs, withDeclaredInputs } from '@mintplayer/ng-spark/renderers';
import {
  AttributeGroup,
  AttributeTab,
  EntityAttributeDefinition,
  EntityType,
  LookupReference,
  PersistentObject,
  QueryResultItem,
  ShowedOn,
  hasShowedOnFlag,
  isPrimitiveArrayAttribute,
} from '@mintplayer/ng-spark/models';

/**
 * Draws a loaded object's attributes the way its detail page does (#601, D20): its tabs, their group cards and the
 * attribute rows, renderers and AsDetail tables included, or with `compact` one flat grid of its `Summary` fields.
 *
 * It does not load the object, and draws no action bar, title, sub-queries or panels: `spark-po-detail` puts those
 * around it, and a custom page for one type (the song page) puts its own. It loads only what the attributes need to
 * be labelled (lookup values, AsDetail row types and their reference options), once per object, and holds the server
 * render until those are answered.
 */
@Component({
  selector: 'spark-po-presenter',
  imports: [CommonModule, NgTemplateOutlet, NgComponentOutlet, RouterModule, BsCardComponent, BsCardHeaderComponent, BsGridComponent, BsGridRowDirective, BsGridColumnDirective, BsTableComponent, BsTabControlComponent, BsTabPageComponent, BsTabPageHeaderDirective, SparkGridCellComponent, SparkAttributeDescriptionComponent, ResolveTranslationPipe, TranslateKeyPipe, AttributeValuePipe, OrDashPipe, LinesPipe, RawAttributeValuePipe, AsDetailColumnsPipe, AsDetailRowRendererPipe, AsDetailLeadingRendererPipe, AsDetailCellValuePipe, ArrayValuePipe, ReferenceLinkRoutePipe, ReferenceChipsPipe, ParsedDatePipe],
  templateUrl: './spark-po-presenter.component.html',
  styleUrl: './spark-po-presenter.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkPoPresenterComponent {
  private readonly sparkService = inject(SparkService);
  private readonly pendingTasks = inject(PendingTasks);
  protected readonly rendererRegistry = inject(SPARK_ATTRIBUTE_RENDERERS);

  /** The loaded object. */
  readonly item = input.required<PersistentObject>();
  /** Its type. */
  readonly entityType = input.required<EntityType>();
  /** The whole catalogue: reference links, and the AsDetail row types it lists. */
  readonly allEntityTypes = input<EntityType[]>([]);
  /** One flat grid of the `Summary` attributes (all detail attributes when none is), no tabs or cards: a tile. */
  readonly compact = input(false);

  readonly lookupReferenceOptions = signal<Record<string, LookupReference>>({});
  readonly asDetailTypes = signal<Record<string, EntityType>>({});
  readonly asDetailReferenceOptions = signal<Record<string, Record<string, QueryResultItem[]>>>({});

  /**
   * What the secondary reads depend on: the object's identity, not its every version. A live push or a server
   * patch replaces the object with the same id, and must not ask for the lookups again.
   *
   * Compared by ids, never by reference: every reload of the page fetches the catalogue afresh, so the type and the
   * catalogue arrive as new objects with the same content. Compared by reference, each reload cleared the AsDetail
   * row types, the tables lost their columns until the answers came back, and every cell was destroyed and drawn
   * again, a row renderer's links included: a click in that gap went nowhere (E2E
   * `QnAContributionsBrowserTests`, the History link, CI runs 42528696421 and 42532593223).
   */
  private readonly target = computed(
    () => `${this.item().id}\n${this.entityType().id}\n${this.allEntityTypes().map(t => t.id).join(',')}`,
  );
  private generation = 0;

  constructor() {
    effect(() => {
      this.target();
      untracked(() => void this.loadLabels());
    });
  }

  /**
   * Drawn from the loaded object's own `showedOn`, falling back to the model's (#264, G-Q3): an action sets
   * it per object in `OnLoadAsync` — a stolen car's police report number is shown, the model says `None`.
   */
  readonly visibleAttributes = computed(() => {
    const loaded = new Map((this.item().attributes ?? []).map(a => [a.name, a] as const));
    return this.entityType().attributes
      .filter(a => hasShowedOnFlag(loaded.get(a.name)?.showedOn ?? a.showedOn, ShowedOn.PersistentObject))
      .sort((a, b) => a.order - b.order);
  });

  /** The compact view's fields: those marked `Summary`, by order, or every detail field when the type marks none. */
  readonly summaryAttributes = computed(() => {
    const loaded = new Map((this.item().attributes ?? []).map(a => [a.name, a] as const));
    const summary = this.entityType().attributes
      .filter(a => hasShowedOnFlag(loaded.get(a.name)?.showedOn ?? a.showedOn, ShowedOn.Summary))
      .sort((a, b) => a.order - b.order);
    return summary.length ? summary : this.visibleAttributes();
  });

  private static readonly DEFAULT_TAB: AttributeTab = { id: '__default__', name: 'Algemeen', label: { nl: 'Algemeen', en: 'General' }, order: 0 };

  readonly ungroupedAttributes = computed(() => {
    const attrs = this.visibleAttributes();
    const groupIds = new Set((this.entityType().groups || []).map(g => g.id));
    return attrs.filter(a => !a.group || !groupIds.has(a.group));
  });

  readonly resolvedTabs = computed((): AttributeTab[] => {
    const et = this.entityType();
    const definedTabs = et.tabs?.length ? [...et.tabs].sort((a, b) => a.order - b.order) : [];
    const hasUngroupedAttrs = this.ungroupedAttributes().length > 0;
    const hasUntabbedGroups = (et.groups || []).some(g => !g.tab);

    if (hasUngroupedAttrs || hasUntabbedGroups || definedTabs.length === 0) {
      return [SparkPoPresenterComponent.DEFAULT_TAB, ...definedTabs];
    }
    return definedTabs;
  });

  groupsForTab(tab: AttributeTab): AttributeGroup[] {
    const groups = this.entityType().groups || [];
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

  /** The lookups and the AsDetail row types of the object on screen; a newer object's answers win. */
  private async loadLabels(): Promise<void> {
    const generation = ++this.generation;
    const stale = () => generation !== this.generation;
    this.lookupReferenceOptions.set({});
    this.asDetailTypes.set({});
    this.asDetailReferenceOptions.set({});
    // The server render waits for the labels, so the page it sends is the labelled one.
    const done = this.pendingTasks.add();
    try {
      await Promise.all([this.loadLookupReferenceOptions(stale), this.loadAsDetailTypes(stale)]);
    } finally {
      done();
    }
  }

  private async loadLookupReferenceOptions(stale: () => boolean): Promise<void> {
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
    if (stale()) return;
    this.lookupReferenceOptions.set(entries.reduce(
      (acc, [k, v]) => v ? { ...acc, [k]: v } : acc,
      {} as Record<string, LookupReference>));
  }

  /** Whether a row of the AsDetail attribute holds a reference in the column that the server sent no breadcrumb for. */
  private needsReferenceLabels(attribute: string, column: string): boolean {
    const rows = this.item().attributes.find(a => a.name === attribute)?.objects ?? [];
    return rows.some(row => {
      const reference = row.attributes.find(a => a.name === column);
      return reference?.value != null && reference.value !== '' && !reference.breadcrumb;
    });
  }

  private async loadAsDetailTypes(stale: () => boolean): Promise<void> {
    const asDetailAttrs = this.visibleAttributes().filter(a => a.dataType === 'AsDetail' && a.isArray && a.asDetailType);
    if (asDetailAttrs.length === 0) return;

    const types = this.allEntityTypes();
    const entityType = this.entityType();
    const parentId = this.item().id;
    const parentType = entityType.alias || entityType.name;
    const newAsDetailTypes: Record<string, EntityType> = {};
    // Same fallback as the form: a row type is usually absent from the Query-gated catalogue, and
    // the server now ships its definition alongside the parent (#385).
    const embedded = entityType.detailTypes ?? [];

    for (const attr of asDetailAttrs) {
      const asDetailType =
        types.find(t => t.clrType === attr.asDetailType)
        ?? embedded.find(t => t.clrType === attr.asDetailType);
      if (asDetailType) {
        newAsDetailTypes[attr.name] = asDetailType;
        // A cell shows the breadcrumb the server resolved (#185); the reference query's options only label a
        // reference that came without one. Fetching them for every row cost the song page 14 of its 18 database
        // requests (docs/spark_ssr_PRD.md I26).
        const refCols = asDetailType.attributes.filter(a => a.dataType === 'Reference' && a.query
          && this.needsReferenceLabels(attr.name, a.name));
        if (refCols.length > 0) {
          const refEntries = await Promise.all(
            refCols.map(async col => {
              const result = await this.sparkService.executeQueryByName(col.query!, { parentId, parentType });
              return [col.name, result.items] as const;
            })
          );
          if (stale()) return;
          this.asDetailReferenceOptions.update(prev => ({
            ...prev,
            [attr.name]: refEntries.reduce((acc, [k, v]) => ({ ...acc, [k]: v }), {} as Record<string, QueryResultItem[]>)
          }));
        }
      }
    }
    if (stale()) return;
    this.asDetailTypes.set(newAsDetailTypes);
  }
}
