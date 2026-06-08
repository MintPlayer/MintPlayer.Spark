import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, Type } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsDatatableComponent, BsDatatableColumnDirective, BsRowTemplateDirective, DatatableSettings, type BsDatatableFetch } from '@mintplayer/ng-bootstrap/datatable';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { SortColumn } from '@mintplayer/pagination';
import { SparkService } from '@mintplayer/ng-spark/services';
import { ResolveTranslationPipe, AttributeValuePipe, ReferenceChipsPipe } from '@mintplayer/ng-spark/pipes';
import { NgComponentOutlet } from '@angular/common';
import { SPARK_ATTRIBUTE_RENDERERS } from '@mintplayer/ng-spark/renderers';
import {
  EntityType,
  EntityAttributeDefinition,
  LookupReference,
  PersistentObject,
  SparkQuery,
  ShowedOn,
  hasShowedOnFlag,
} from '@mintplayer/ng-spark/models';

@Component({
  selector: 'spark-sub-query',
  imports: [CommonModule, NgComponentOutlet, RouterModule, BsCardComponent, BsCardHeaderComponent, BsDatatableComponent, BsDatatableColumnDirective, BsRowTemplateDirective, BsSpinnerComponent, ResolveTranslationPipe, AttributeValuePipe, ReferenceChipsPipe],
  templateUrl: './spark-sub-query.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class SparkSubQueryComponent {
  private readonly sparkService = inject(SparkService);
  private readonly rendererRegistry = inject(SPARK_ATTRIBUTE_RENDERERS);

  queryId = input.required<string>();
  parentId = input.required<string>();
  parentType = input.required<string>();

  query = signal<SparkQuery | null>(null);
  entityType = signal<EntityType | null>(null);
  allEntityTypes = signal<EntityType[]>([]);
  resultCount = signal<number | null>(null);
  lookupReferenceOptions = signal<Record<string, LookupReference>>({});
  loading = signal(true);
  canRead = signal(false);
  settings = signal(new DatatableSettings({
    perPage: { values: [10, 25, 50], selected: 10 },
    page: { values: [1], selected: 1 },
    sortColumns: []
  }));
  fetchFn = signal<BsDatatableFetch<PersistentObject> | null>(null);
  isVirtualScrolling = computed(() => this.query()?.renderMode === 'VirtualScrolling');

  visibleAttributes = computed(() => {
    return this.entityType()?.attributes
      .filter(a => a.isVisible && hasShowedOnFlag(a.showedOn, ShowedOn.Query))
      .sort((a, b) => a.order - b.order) || [];
  });

  constructor() {
    effect(() => {
      const qId = this.queryId();
      const pId = this.parentId();
      const pType = this.parentType();
      if (qId && pId && pType) {
        this.loadData(qId, pId, pType);
      }
    });
  }

  private async loadData(queryId: string, parentId: string, parentType: string): Promise<void> {
    this.loading.set(true);
    this.resultCount.set(null);
    this.fetchFn.set(null);
    try {
      const [resolvedQuery, entityTypes] = await Promise.all([
        this.sparkService.getQuery(queryId),
        this.sparkService.getEntityTypes()
      ]);

      this.query.set(resolvedQuery);
      this.allEntityTypes.set(entityTypes);

      const initialSortColumns: SortColumn[] = (resolvedQuery.sortColumns || []).map(sc => ({
        property: sc.property,
        direction: sc.direction === 'desc' ? 'descending' as const : 'ascending' as const
      }));

      // Resolve entity type from query's entityType field
      if (resolvedQuery.entityType) {
        const et = entityTypes.find(t =>
          t.name === resolvedQuery.entityType || t.alias === resolvedQuery.entityType?.toLowerCase()
        );
        this.entityType.set(et || null);
        if (et) {
          const permissions = await this.sparkService.getPermissions(et.id);
          this.canRead.set(permissions.canRead);
        }
      }

      this.settings.set(new DatatableSettings({
        perPage: { values: [10, 25, 50], selected: 10 },
        page: { values: [1], selected: 1 },
        sortColumns: initialSortColumns
      }));
      // The datatable drives paging/sorting via [(settings)] and calls fetchFn
      // per page. Virtual scrolling is just the [virtualScroll] template flag.
      this.fetchFn.set(this.makeFetch(resolvedQuery, parentId, parentType));

      this.loadLookupReferenceOptions();
    } catch {
      this.fetchFn.set(null);
    } finally {
      this.loading.set(false);
    }
  }

  private makeFetch(query: SparkQuery, parentId: string, parentType: string): BsDatatableFetch<PersistentObject> {
    return (req) => this.sparkService.executeQuery(query.id, {
      sortColumns: req.sortColumns,
      skip: (req.page - 1) * req.perPage,
      take: req.perPage,
      parentId, parentType,
    }).then(r => {
      this.resultCount.set(r.totalRecords);
      return {
        data: r.data,
        totalRecords: r.totalRecords,
        totalPages: Math.ceil(r.totalRecords / req.perPage) || 1,
        perPage: req.perPage,
        page: req.page,
      };
    }).catch(() => {
      this.resultCount.set(0);
      return { data: [], totalRecords: 0, totalPages: 1, perPage: req.perPage, page: req.page };
    });
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
}
