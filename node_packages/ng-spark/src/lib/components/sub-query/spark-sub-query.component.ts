import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, Type } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsDatatableComponent, BsDatatableColumnDirective, BsRowTemplateDirective, DatatableSettings } from '@mintplayer/ng-bootstrap/datatable';
import { BsVirtualDatatableComponent, BsVirtualRowTemplateDirective, VirtualDatatableDataSource } from '@mintplayer/ng-bootstrap/virtual-datatable';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { PaginationResponse, SortColumn } from '@mintplayer/pagination';
import { SparkService } from '../../services/spark.service';
import { ResolveTranslationPipe } from '../../pipes/resolve-translation.pipe';
import { TranslateKeyPipe } from '../../pipes/translate-key.pipe';
import { AttributeValuePipe } from '../../pipes/attribute-value.pipe';
import { NgComponentOutlet } from '@angular/common';
import { SPARK_ATTRIBUTE_RENDERERS } from '../../providers/spark-attribute-renderer-registry';
import { EntityType, EntityAttributeDefinition } from '../../models/entity-type';
import { LookupReference } from '../../models/lookup-reference';
import { PersistentObject } from '../../models/persistent-object';
import { SparkQuery } from '../../models/spark-query';
import { ShowedOn, hasShowedOnFlag } from '../../models/showed-on';

@Component({
  selector: 'spark-sub-query',
  imports: [CommonModule, NgComponentOutlet, RouterModule, BsCardComponent, BsCardHeaderComponent, BsDatatableComponent, BsDatatableColumnDirective, BsRowTemplateDirective, BsVirtualDatatableComponent, BsVirtualRowTemplateDirective, BsTableComponent, BsSpinnerComponent, ResolveTranslationPipe, TranslateKeyPipe, AttributeValuePipe],
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
  paginationData = signal<PaginationResponse<PersistentObject> | undefined>(undefined);
  lookupReferenceOptions = signal<Record<string, LookupReference>>({});
  loading = signal(true);
  canRead = signal(false);
  settings = signal(new DatatableSettings({
    perPage: { values: [10, 25, 50], selected: 10 },
    page: { values: [1], selected: 1 },
    sortColumns: []
  }));
  virtualDataSource = signal<VirtualDatatableDataSource<PersistentObject> | null>(null);
  virtualSettings = signal(new DatatableSettings({
    sortColumns: []
  }));

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

      if (resolvedQuery.renderMode === 'VirtualScrolling') {
        this.virtualSettings.set(new DatatableSettings({ sortColumns: initialSortColumns }));
        this.virtualDataSource.set(new VirtualDatatableDataSource<PersistentObject>(
          (skip, take) => this.sparkService.executeQuery(resolvedQuery.id, {
            sortColumns: this.virtualSettings().sortColumns,
            skip, take,
            parentId, parentType,
          }).then(r => ({ data: r.data, totalRecords: r.totalRecords })),
          50
        ));
      } else {
        this.settings.set(new DatatableSettings({
          perPage: { values: [10, 25, 50], selected: 10 },
          page: { values: [1], selected: 1 },
          sortColumns: initialSortColumns
        }));
        await this.loadPage(resolvedQuery.id, parentId, parentType);
      }

      this.loadLookupReferenceOptions();
    } catch {
      this.paginationData.set(undefined);
    } finally {
      this.loading.set(false);
    }
  }

  private async loadPage(queryId: string, parentId: string, parentType: string): Promise<void> {
    const s = this.settings();
    const result = await this.sparkService.executeQuery(queryId, {
      sortColumns: s.sortColumns,
      skip: (s.page.selected - 1) * s.perPage.selected,
      take: s.perPage.selected,
      parentId, parentType,
    });

    const totalPages = Math.ceil(result.totalRecords / s.perPage.selected) || 1;
    this.paginationData.set({
      data: result.data,
      totalRecords: result.totalRecords,
      totalPages,
      perPage: s.perPage.selected,
      page: s.page.selected,
    });
    s.page.values = Array.from({ length: totalPages }, (_, i) => i + 1);
  }

  onSettingsChange(): void {
    const q = this.query();
    if (!q) return;
    if (q.renderMode === 'VirtualScrolling') {
      this.virtualDataSource()?.reset();
      const pId = this.parentId();
      const pType = this.parentType();
      this.virtualDataSource.set(new VirtualDatatableDataSource<PersistentObject>(
        (skip, take) => this.sparkService.executeQuery(q.id, {
          sortColumns: this.virtualSettings().sortColumns,
          skip, take,
          parentId: pId, parentType: pType,
        }).then(r => ({ data: r.data, totalRecords: r.totalRecords })),
        50
      ));
    } else {
      this.loadPage(q.id, this.parentId(), this.parentType());
    }
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
