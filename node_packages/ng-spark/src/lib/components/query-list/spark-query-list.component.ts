import { ChangeDetectionStrategy, Component, computed, DestroyRef, effect, inject, input, output, signal, TemplateRef, Type } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Subscription } from 'rxjs';
import { CommonModule, NgTemplateOutlet } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsDatatableComponent, BsDatatableColumnDirective, BsRowTemplateDirective, DatatableSettings } from '@mintplayer/ng-bootstrap/datatable';
import { BsVirtualDatatableComponent, BsVirtualRowTemplateDirective, VirtualDatatableDataSource } from '@mintplayer/ng-bootstrap/virtual-datatable';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsContainerComponent } from '@mintplayer/ng-bootstrap/container';
import { BsGridComponent, BsGridRowDirective, BsGridColumnDirective } from '@mintplayer/ng-bootstrap/grid';
import { BsInputGroupComponent } from '@mintplayer/ng-bootstrap/input-group';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { PaginationResponse, SortColumn } from '@mintplayer/pagination';
import { SparkService } from '../../services/spark.service';
import { SparkStreamingService } from '../../services/spark-streaming.service';
import { SparkDataRefreshService } from '../../services/spark-data-refresh.service';
import { StreamingMessage } from '../../models/streaming-message';
import { SparkIconComponent } from '../icon/spark-icon.component';
import { TranslateKeyPipe } from '../../pipes/translate-key.pipe';
import { ResolveTranslationPipe } from '../../pipes/resolve-translation.pipe';
import { NgComponentOutlet } from '@angular/common';
import { AttributeValuePipe } from '../../pipes/attribute-value.pipe';
import { SPARK_ATTRIBUTE_RENDERERS } from '../../providers/spark-attribute-renderer-registry';
import { EntityType, EntityAttributeDefinition } from '../../models/entity-type';
import { LookupReference } from '../../models/lookup-reference';
import { PersistentObject } from '../../models/persistent-object';
import { SparkQuery } from '../../models/spark-query';
import { ShowedOn, hasShowedOnFlag } from '../../models/showed-on';

@Component({
  selector: 'spark-query-list',
  imports: [CommonModule, NgTemplateOutlet, NgComponentOutlet, FormsModule, RouterModule, BsAlertComponent, BsContainerComponent, BsDatatableComponent, BsDatatableColumnDirective, BsRowTemplateDirective, BsVirtualDatatableComponent, BsVirtualRowTemplateDirective, BsFormComponent, BsFormControlDirective, BsGridComponent, BsGridRowDirective, BsGridColumnDirective, BsInputGroupComponent, BsSpinnerComponent, SparkIconComponent, ResolveTranslationPipe, TranslateKeyPipe, AttributeValuePipe],
  templateUrl: './spark-query-list.component.html',
  styleUrl: './spark-query-list.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '[class.virtual-scrolling]': 'isVirtualScrolling()'
  }
})
export class SparkQueryListComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly sparkService = inject(SparkService);
  private readonly streamingService = inject(SparkStreamingService);
  private readonly rendererRegistry = inject(SPARK_ATTRIBUTE_RENDERERS);
  private readonly destroyRef = inject(DestroyRef);
  private readonly refreshService = inject(SparkDataRefreshService);

  extraActionsTemplate = input<TemplateRef<void> | null>(null);

  rowClicked = output<PersistentObject>();
  createClicked = output<void>();

  colors = Color;
  errorMessage = signal<string | null>(null);
  query = signal<SparkQuery | null>(null);
  entityType = signal<EntityType | null>(null);
  allEntityTypes = signal<EntityType[]>([]);
  lookupReferenceOptions = signal<Record<string, LookupReference>>({});
  paginationData = signal<PaginationResponse<PersistentObject> | undefined>(undefined);
  searchTerm: string = '';
  canRead = signal(false);
  canCreate = signal(false);
  isStreaming = signal(false);
  private streamingSub: Subscription | null = null;
  private allItems = signal<PersistentObject[]>([]);
  private filteredItems: PersistentObject[] = [];
  settings = signal(new DatatableSettings({
    perPage: { values: [10, 25, 50], selected: 10 },
    page: { values: [1], selected: 1 },
    sortColumns: []
  }));
  virtualDataSource = signal<VirtualDatatableDataSource<PersistentObject> | null>(null);
  virtualSettings = signal(new DatatableSettings({
    sortColumns: []
  }));

  constructor() {
    this.route.paramMap.pipe(takeUntilDestroyed()).subscribe(params => this.onParamsChange(params));
    this.destroyRef.onDestroy(() => this.disconnectStreaming());

    // Re-fetch query results when external data changes
    effect(() => {
      const event = this.refreshService.refreshTrigger();
      if (event && this.query()) {
        this.loadItems();
      }
    });
  }

  private async onParamsChange(params: any): Promise<void> {
    const queryId = params.get('queryId');
    const typeParam = params.get('type');

    let resolvedQuery: SparkQuery | null = null;
    let resolvedEntityType: EntityType | null = null;
    let resolvedEntityTypes: EntityType[] = [];

    if (queryId) {
      // First check if queryId matches a program unit alias, and use its queryId
      let effectiveQueryId = queryId;
      const config = await this.sparkService.getProgramUnits();
      for (const group of config.programUnitGroups) {
        const unit = group.programUnits.find(u => u.alias === queryId && u.type === 'query' && u.queryId);
        if (unit) {
          effectiveQueryId = unit.queryId!;
          break;
        }
      }
      resolvedQuery = await this.sparkService.getQuery(effectiveQueryId);
      if (resolvedQuery) {
        const result = await this.resolveEntityTypeForQuery(resolvedQuery);
        resolvedEntityType = result.entityType;
        resolvedEntityTypes = result.entityTypes;
      }
    } else if (typeParam) {
      resolvedEntityTypes = await this.sparkService.getEntityTypes();
      resolvedEntityType = resolvedEntityTypes.find(t =>
        t.id === typeParam || t.alias === typeParam
      ) || null;

      if (resolvedEntityType) {
        const queries = await this.sparkService.getQueries();
        const singularName = resolvedEntityType.name;
        resolvedQuery = queries.find(q => {
          // Match by explicit entityType
          if (q.entityType === singularName) return true;
          // Match by source name
          const sourceName = this.extractSourceName(q.source);
          const sourceSingular = this.singularize(sourceName);
          return sourceName === singularName ||
            sourceSingular === singularName ||
            sourceName === singularName + 's';
        }) || null;
      }
    }

    if (resolvedQuery) this.query.set(resolvedQuery);

    if (resolvedEntityType) {
      this.entityType.set(resolvedEntityType);
      this.allEntityTypes.set(resolvedEntityTypes);

      const initialSortColumns: SortColumn[] = (resolvedQuery?.sortColumns || []).map(sc => ({
        property: sc.property,
        direction: sc.direction === 'desc' ? 'descending' as const : 'ascending' as const
      }));

      if (resolvedQuery?.renderMode === 'VirtualScrolling') {
        this.virtualSettings.set(new DatatableSettings({ sortColumns: initialSortColumns }));
        if (resolvedQuery?.isStreamingQuery) {
          // Streaming + VirtualScrolling: create a stable client-side data source, then connect WebSocket
          this.virtualDataSource.set(new VirtualDatatableDataSource<PersistentObject>(
            (skip, take) => Promise.resolve({
              data: this.filteredItems.slice(skip, skip + take),
              totalRecords: this.filteredItems.length
            }),
            50
          ));
          this.connectStreaming(resolvedQuery.id);
        } else {
          this.initVirtualDataSource();
        }
      } else {
        this.settings.set(new DatatableSettings({
          perPage: { values: [10, 25, 50], selected: 10 },
          page: { values: [1], selected: 1 },
          sortColumns: initialSortColumns
        }));
        await this.loadItems();
      }

      this.loadLookupReferenceOptions();
      const permissions = await this.sparkService.getPermissions(resolvedEntityType.id);
      this.canRead.set(permissions.canRead);
      this.canCreate.set(permissions.canCreate);
    }
  }

  private async resolveEntityTypeForQuery(query: SparkQuery): Promise<{ entityType: EntityType | null; entityTypes: EntityType[] }> {
    const entityTypes = await this.sparkService.getEntityTypes();

    // If entityType is explicitly set on the query, use it directly
    if (query.entityType) {
      const type = entityTypes.find(t =>
        t.name === query.entityType || t.alias === query.entityType?.toLowerCase()
      );
      return { entityType: type || null, entityTypes };
    }

    // For Database.X sources, extract the property name and try to match
    const sourceName = this.extractSourceName(query.source);
    const singularName = this.singularize(sourceName);
    const type = entityTypes.find(t =>
      t.name === sourceName ||
      t.name === singularName ||
      t.clrType.endsWith(singularName)
    );
    return { entityType: type || null, entityTypes };
  }

  private extractSourceName(source: string): string {
    const dotIndex = source.indexOf('.');
    return dotIndex >= 0 ? source.substring(dotIndex + 1) : source;
  }

  private singularize(plural: string): string {
    const irregulars: { [key: string]: string } = {
      'People': 'Person',
      'Children': 'Child',
      'Men': 'Men',
      'Women': 'Woman'
    };
    if (irregulars[plural]) return irregulars[plural];

    if (plural.endsWith('ies')) {
      return plural.slice(0, -3) + 'y';
    }
    if (plural.endsWith('es')) {
      return plural.slice(0, -2);
    }
    if (plural.endsWith('s')) {
      return plural.slice(0, -1);
    }
    return plural;
  }

  private initVirtualDataSource(): void {
    const currentQuery = this.query();
    if (!currentQuery) return;
    this.virtualDataSource.set(new VirtualDatatableDataSource<PersistentObject>(
      (skip, take) => this.sparkService.executeQuery(currentQuery.id, {
        sortColumns: this.virtualSettings().sortColumns,
        skip, take,
        search: this.searchTerm || undefined,
      }).then(r => ({ data: r.data, totalRecords: r.totalRecords })),
      50
    ));
  }

  async loadItems(): Promise<void> {
    const currentQuery = this.query();
    if (!currentQuery) return;

    // Streaming queries use WebSocket instead of HTTP
    if (currentQuery.isStreamingQuery) {
      this.connectStreaming(currentQuery.id);
      return;
    }

    // Non-streaming: disconnect any previous streaming connection
    this.disconnectStreaming();

    try {
      const s = this.settings();
      const result = await this.sparkService.executeQuery(currentQuery.id, {
        sortColumns: s.sortColumns,
        skip: (s.page.selected - 1) * s.perPage.selected,
        take: s.perPage.selected,
        search: this.searchTerm || undefined,
      });
      this.errorMessage.set(null);

      const totalPages = Math.ceil(result.totalRecords / s.perPage.selected) || 1;
      this.paginationData.set({
        data: result.data,
        totalRecords: result.totalRecords,
        totalPages: totalPages,
        perPage: s.perPage.selected,
        page: s.page.selected
      });
      s.page.values = Array.from({ length: totalPages }, (_, i) => i + 1);
    } catch (e: any) {
      this.errorMessage.set(e.error?.error || e.message || 'An unexpected error occurred');
      this.paginationData.set(undefined);
    }
  }

  onSettingsChange(): void {
    if (this.isStreaming()) {
      // Streaming: sort/filter is client-side only
      this.applyFilter();
      return;
    }

    if (this.query()?.renderMode === 'VirtualScrolling') {
      this.virtualDataSource()?.reset();
      this.initVirtualDataSource();
    } else {
      this.loadItems();
    }
  }

  onSearchChange(): void {
    if (this.isStreaming()) {
      this.applyFilter();
      return;
    }

    if (this.query()?.renderMode === 'VirtualScrolling') {
      this.virtualDataSource()?.reset();
      this.initVirtualDataSource();
    } else {
      this.settings().page.selected = 1;
      this.loadItems();
    }
  }

  clearSearch(): void {
    this.searchTerm = '';
    this.onSearchChange();
  }

  isVirtualScrolling = computed(() => this.query()?.renderMode === 'VirtualScrolling');

  visibleAttributes = computed(() => {
    return this.entityType()?.attributes
      .filter(a => a.isVisible && hasShowedOnFlag(a.showedOn, ShowedOn.Query))
      .sort((a, b) => a.order - b.order) || [];
  });

  getColumnRendererComponent(attr: EntityAttributeDefinition): Type<any> | null {
    if (!attr.renderer) return null;
    return this.rendererRegistry.find(r => r.name === attr.renderer)?.columnComponent ?? null;
  }

  getColumnRendererInputs(item: PersistentObject, attr: EntityAttributeDefinition): Record<string, any> {
    const itemAttr = item.attributes.find(a => a.name === attr.name);
    return {
      value: itemAttr?.value,
      attribute: attr,
      options: attr.rendererOptions,
    };
  }

  private async loadLookupReferenceOptions(): Promise<void> {
    const lookupAttrs = this.visibleAttributes().filter(a => a.lookupReferenceType);
    if (lookupAttrs.length === 0) return;

    const lookupNames = [...new Set(lookupAttrs.map(a => a.lookupReferenceType!))];
    const entries = await Promise.all(
      lookupNames.map(async name => {
        const result = await this.sparkService.getLookupReference(name);
        return [name, result] as const;
      })
    );
    this.lookupReferenceOptions.set(entries.reduce((acc, [k, v]) => ({ ...acc, [k]: v }), {} as Record<string, LookupReference>));
  }

  onCreate(): void {
    this.createClicked.emit();
    const et = this.entityType();
    if (et) {
      this.router.navigate(['/po', et.alias || et.id, 'new']);
    }
  }

  private connectStreaming(queryId: string): void {
    this.disconnectStreaming();
    this.isStreaming.set(true);

    this.streamingSub = this.streamingService.connectToStreamingQuery(queryId).subscribe({
      next: (message) => this.handleStreamingMessage(message),
      error: (err) => {
        this.errorMessage.set(err?.message || 'Streaming connection failed');
        this.isStreaming.set(false);
      },
      complete: () => {
        this.isStreaming.set(false);
      }
    });
  }

  private disconnectStreaming(): void {
    if (this.streamingSub) {
      this.streamingSub.unsubscribe();
      this.streamingSub = null;
    }
    this.isStreaming.set(false);
  }

  private handleStreamingMessage(message: StreamingMessage): void {
    switch (message.type) {
      case 'snapshot':
        this.errorMessage.set(null);
        this.allItems.set(message.data);
        this.applyFilter();
        break;

      case 'patch':
        if (message.updated.length > 0) {
          const currentItems = this.allItems();
          const updatedItems = currentItems.map(item => {
            const patch = message.updated.find(u => u.id === item.id);
            if (!patch) return item;

            // Clone the item and update only changed attribute values
            const updatedAttributes = item.attributes.map(attr => {
              if (attr.name in patch.attributes) {
                return { ...attr, value: patch.attributes[attr.name] };
              }
              return attr;
            });

            return { ...item, attributes: updatedAttributes };
          });

          this.allItems.set(updatedItems);
          this.applyFilter();
        }
        break;

      case 'error':
        this.errorMessage.set(message.message);
        break;
    }
  }

  private applyFilter(): void {
    let items = this.allItems();

    // Apply search filter
    if (this.searchTerm) {
      const term = this.searchTerm.toLowerCase();
      items = items.filter(item =>
        item.attributes.some(a => String(a.value ?? '').toLowerCase().includes(term))
      );
    }

    // Apply sorting
    const isVirtual = this.query()?.renderMode === 'VirtualScrolling';
    const sortCols = isVirtual ? this.virtualSettings().sortColumns : this.settings().sortColumns;
    if (sortCols.length > 0) {
      items = [...items].sort((a, b) => {
        for (const col of sortCols) {
          const aVal = a.attributes.find(attr => attr.name === col.property)?.value ?? '';
          const bVal = b.attributes.find(attr => attr.name === col.property)?.value ?? '';
          const cmp = String(aVal).localeCompare(String(bVal));
          if (cmp !== 0) return col.direction === 'descending' ? -cmp : cmp;
        }
        return 0;
      });
    }

    if (isVirtual) {
      // Update the mutable filtered items array.
      // The stable data source's fetchFn closure reads from this.filteredItems,
      // so clearing its cache and emitting empty triggers the CDK viewport to re-fetch.
      this.filteredItems = items;
      this.virtualDataSource()?.reset();
    } else {
      this.paginationData.set({
        data: items,
        totalRecords: items.length,
        totalPages: 1,
        perPage: items.length,
        page: 1
      });
    }
  }
}
