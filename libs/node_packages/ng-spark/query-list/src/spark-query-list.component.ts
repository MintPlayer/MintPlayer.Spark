import { ChangeDetectionStrategy, Component, computed, DestroyRef, inject, input, output, signal, TemplateRef, Type } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Subscription } from 'rxjs';
import { CommonModule, NgTemplateOutlet, NgComponentOutlet } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsDatatableComponent, BsDatatableColumnDirective, BsRowTemplateDirective, DatatableSettings, type BsDatatableFetch } from '@mintplayer/ng-bootstrap/datatable';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsGridComponent, BsGridRowDirective, BsGridColumnDirective } from '@mintplayer/ng-bootstrap/grid';
import { BsInputGroupComponent } from '@mintplayer/ng-bootstrap/input-group';
import { BsPriorityNavComponent, BsPriorityNavItemDirective } from '@mintplayer/ng-bootstrap/priority-nav';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { HttpErrorResponse } from '@angular/common/http';
import { SortColumn } from '@mintplayer/pagination';
import { SparkService, SparkStreamingService, SparkLanguageService } from '@mintplayer/ng-spark/services';
import {
  TranslateKeyPipe,
  ResolveTranslationPipe,
  AttributeValuePipe,
  ReferenceChipsPipe,
} from '@mintplayer/ng-spark/pipes';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';
import { SPARK_ATTRIBUTE_RENDERERS } from '@mintplayer/ng-spark/renderers';
import {
  CustomActionDefinition,
  StreamingMessage,
  EntityType,
  EntityAttributeDefinition,
  LookupReference,
  PersistentObject,
  SparkQuery,
  ShowedOn,
  hasShowedOnFlag,
} from '@mintplayer/ng-spark/models';

@Component({
  selector: 'spark-query-list',
  imports: [CommonModule, NgTemplateOutlet, NgComponentOutlet, FormsModule, RouterModule, BsAlertComponent, BsDatatableComponent, BsDatatableColumnDirective, BsRowTemplateDirective, BsFormComponent, BsFormControlDirective, BsGridComponent, BsGridRowDirective, BsGridColumnDirective, BsInputGroupComponent, BsPriorityNavComponent, BsPriorityNavItemDirective, BsSpinnerComponent, SparkIconComponent, ResolveTranslationPipe, TranslateKeyPipe, AttributeValuePipe, ReferenceChipsPipe],
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
  protected readonly lang = inject(SparkLanguageService);
  private readonly rendererRegistry = inject(SPARK_ATTRIBUTE_RENDERERS);
  private readonly destroyRef = inject(DestroyRef);

  extraActionsTemplate = input<TemplateRef<void> | null>(null);
  showCustomActions = input(true);

  rowClicked = output<PersistentObject>();
  createClicked = output<void>();
  customActionExecuted = output<{ action: CustomActionDefinition }>();

  colors = Color;
  errorMessage = signal<string | null>(null);
  query = signal<SparkQuery | null>(null);
  entityType = signal<EntityType | null>(null);
  allEntityTypes = signal<EntityType[]>([]);
  lookupReferenceOptions = signal<Record<string, LookupReference>>({});
  resultCount = signal<number | null>(null);
  searchTerm: string = '';
  canRead = signal(false);
  canCreate = signal(false);
  customActions = signal<CustomActionDefinition[]>([]);
  isStreaming = signal(false);
  private streamingSub: Subscription | null = null;
  private allItems = signal<PersistentObject[]>([]);
  streamItems = signal<PersistentObject[]>([]);
  fetchFn = signal<BsDatatableFetch<PersistentObject> | null>(null);
  settings = signal(new DatatableSettings({
    perPage: { values: [10, 25, 50], selected: 10 },
    page: { values: [1], selected: 1 },
    sortColumns: []
  }));

  constructor() {
    this.route.paramMap.pipe(takeUntilDestroyed()).subscribe(params => this.onParamsChange(params));
    this.destroyRef.onDestroy(() => this.disconnectStreaming());
  }

  private async onParamsChange(params: any): Promise<void> {
    // Reset prior-route state so we render the spinner (not stale rows from
    // the previous query) while the new query/entityType resolve.
    this.entityType.set(null);
    this.fetchFn.set(null);
    this.resultCount.set(null);
    this.allItems.set([]);
    this.streamItems.set([]);
    this.disconnectStreaming();

    const queryId = params.get('queryId');
    const typeParam = params.get('type');

    let resolvedQuery: SparkQuery | null = null;
    let resolvedEntityType: EntityType | null = null;
    let resolvedEntityTypes: EntityType[] = [];

    if (queryId) {
      resolvedQuery = await this.sparkService.getQuery(queryId);
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

      this.settings.set(new DatatableSettings({
        perPage: { values: [10, 25, 50], selected: 10 },
        page: { values: [1], selected: 1 },
        sortColumns: initialSortColumns
      }));

      if (resolvedQuery?.isStreamingQuery) {
        // Streaming: WebSocket feeds allItems; the datatable binds [data]="streamItems()".
        this.connectStreaming(resolvedQuery.id);
      } else if (resolvedQuery) {
        // Non-streaming: the datatable drives paging/sorting via [(settings)] and calls
        // fetchFn per page. Virtual scrolling is just the [virtualScroll] template flag —
        // the datatable front-loads all pages from fetchFn when virtual.
        this.fetchFn.set(this.makeFetch(resolvedQuery));
      }

      this.loadLookupReferenceOptions();
      const [permissions, actions] = await Promise.all([
        this.sparkService.getPermissions(resolvedEntityType.id),
        this.sparkService.getCustomActions(resolvedEntityType.id),
      ]);
      this.canRead.set(permissions.canRead);
      this.canCreate.set(permissions.canCreate);
      this.customActions.set(actions.filter(a => a.showedOn === 'list' || a.showedOn === 'both'));
    }
  }

  async onCustomAction(action: CustomActionDefinition): Promise<void> {
    if (action.confirmationMessageKey) {
      const message = this.lang.t(action.confirmationMessageKey) || 'Are you sure?';
      if (!confirm(message)) return;
    }
    try {
      await this.sparkService.executeCustomAction(this.entityType()!.id, action.name);
      this.customActionExecuted.emit({ action });
      if (action.refreshOnCompleted) {
        this.refresh();
      }
    } catch (e) {
      const err = e as HttpErrorResponse;
      this.errorMessage.set(err.error?.error || err.message || 'Action failed');
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

  /**
   * Builds the server-side fetch callback the datatable invokes per page/sort.
   * Reads `searchTerm` live, so a settings change (or a new fetchFn identity)
   * refetches with the current search term.
   */
  private makeFetch(query: SparkQuery): BsDatatableFetch<PersistentObject> {
    return (req) => this.sparkService.executeQuery(query.id, {
      sortColumns: req.sortColumns,
      skip: (req.page - 1) * req.perPage,
      take: req.perPage,
      search: this.searchTerm || undefined,
    }).then(r => {
      this.errorMessage.set(null);
      this.resultCount.set(r.totalRecords);
      return {
        data: r.data,
        totalRecords: r.totalRecords,
        totalPages: Math.ceil(r.totalRecords / req.perPage) || 1,
        perPage: req.perPage,
        page: req.page,
      };
    }).catch((e: any) => {
      this.errorMessage.set(e.error?.error || e.message || 'An unexpected error occurred');
      this.resultCount.set(0);
      return { data: [], totalRecords: 0, totalPages: 1, perPage: req.perPage, page: req.page };
    });
  }

  /** Force a refetch (e.g. after a custom action) without changing page/sort. */
  private refresh(): void {
    if (this.isStreaming()) {
      this.applyFilter();
      return;
    }
    const q = this.query();
    if (q) this.fetchFn.set(this.makeFetch(q));
  }

  onSearchChange(): void {
    if (this.isStreaming()) {
      this.applyFilter();
      return;
    }
    // Reset to page 1 for the new search.
    const s = this.settings();
    this.settings.set(new DatatableSettings({
      perPage: { values: s.perPage.values, selected: s.perPage.selected },
      page: { values: [1], selected: 1 },
      sortColumns: s.sortColumns,
    }));
    // Re-assign the fetch callback so the datatable refetches even when
    // page/perPage/sort are unchanged. ng-bootstrap 22.4's web component dedupes
    // reloads by {sortColumns, perPage, page}; setting a new fetch identity resets
    // that key (set fetch → _lastReloadKey = null) and forces the reload. makeFetch
    // reads searchTerm live (mirrors refresh()).
    const q = this.query();
    if (q) this.fetchFn.set(this.makeFetch(q));
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

    // Apply sorting (client-side for the streaming snapshot; the datatable in
    // [data] mode also auto-sorts on header clicks).
    const sortCols = this.settings().sortColumns;
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

    this.streamItems.set(items);
    this.resultCount.set(items.length);
  }
}
