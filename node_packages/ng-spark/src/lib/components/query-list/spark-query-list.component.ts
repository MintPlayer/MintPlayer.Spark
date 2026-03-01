import { ChangeDetectionStrategy, Component, computed, ContentChildren, inject, input, output, QueryList, signal, TemplateRef } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule, NgTemplateOutlet } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsDatatableComponent, BsDatatableColumnDirective, BsRowTemplateDirective, DatatableSettings } from '@mintplayer/ng-bootstrap/datatable';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsContainerComponent } from '@mintplayer/ng-bootstrap/container';
import { BsGridComponent, BsGridRowDirective, BsGridColumnDirective } from '@mintplayer/ng-bootstrap/grid';
import { BsInputGroupComponent } from '@mintplayer/ng-bootstrap/input-group';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { PaginationResponse } from '@mintplayer/pagination';
import { SparkService } from '../../services/spark.service';
import { SparkIconComponent } from '../icon/spark-icon.component';
import { TranslateKeyPipe } from '../../pipes/translate-key.pipe';
import { ResolveTranslationPipe } from '../../pipes/resolve-translation.pipe';
import { AttributeValuePipe } from '../../pipes/attribute-value.pipe';
import { SparkColumnTemplateDirective, SparkColumnTemplateContext } from '../../directives/spark-column-template.directive';
import { EntityType, EntityAttributeDefinition } from '../../models/entity-type';
import { LookupReference } from '../../models/lookup-reference';
import { PersistentObject } from '../../models/persistent-object';
import { SparkQuery } from '../../models/spark-query';
import { ShowedOn, hasShowedOnFlag } from '../../models/showed-on';

@Component({
  selector: 'spark-query-list',
  imports: [CommonModule, NgTemplateOutlet, FormsModule, RouterModule, BsAlertComponent, BsContainerComponent, BsDatatableComponent, BsDatatableColumnDirective, BsRowTemplateDirective, BsFormComponent, BsFormControlDirective, BsGridComponent, BsGridRowDirective, BsGridColumnDirective, BsInputGroupComponent, BsSpinnerComponent, SparkIconComponent, ResolveTranslationPipe, TranslateKeyPipe, AttributeValuePipe],
  templateUrl: './spark-query-list.component.html',
  styleUrl: './spark-query-list.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class SparkQueryListComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly sparkService = inject(SparkService);

  @ContentChildren(SparkColumnTemplateDirective) columnTemplates!: QueryList<SparkColumnTemplateDirective>;

  extraActionsTemplate = input<TemplateRef<void> | null>(null);

  rowClicked = output<PersistentObject>();
  createClicked = output<void>();

  colors = Color;
  errorMessage = signal<string | null>(null);
  query = signal<SparkQuery | null>(null);
  entityType = signal<EntityType | null>(null);
  allEntityTypes = signal<EntityType[]>([]);
  allItems = signal<PersistentObject[]>([]);
  lookupReferenceOptions = signal<Record<string, LookupReference>>({});
  paginationData = signal<PaginationResponse<PersistentObject> | undefined>(undefined);
  searchTerm: string = '';
  canCreate = signal(false);
  settings: DatatableSettings = new DatatableSettings({
    perPage: { values: [10, 25, 50], selected: 10 },
    page: { values: [1], selected: 1 },
    sortProperty: '',
    sortDirection: 'ascending'
  });
  private currentSortProperty = '';
  private currentSortDirection = '';

  constructor() {
    this.route.paramMap.pipe(takeUntilDestroyed()).subscribe(params => this.onParamsChange(params));
  }

  private async onParamsChange(params: any): Promise<void> {
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
          const contextSingular = this.singularize(q.contextProperty);
          return q.contextProperty === singularName ||
            contextSingular === singularName ||
            q.contextProperty === singularName + 's';
        }) || null;
      }
    }

    if (resolvedQuery) this.query.set(resolvedQuery);

    if (resolvedEntityType) {
      this.entityType.set(resolvedEntityType);
      this.allEntityTypes.set(resolvedEntityTypes);
      this.settings = new DatatableSettings({
        perPage: { values: [10, 25, 50], selected: 10 },
        page: { values: [1], selected: 1 },
        sortProperty: resolvedQuery?.sortBy || '',
        sortDirection: resolvedQuery?.sortDirection === 'desc' ? 'descending' : 'ascending'
      });
      this.loadLookupReferenceOptions();
      const permissions = await this.sparkService.getPermissions(resolvedEntityType.id);
      this.canCreate.set(permissions.canCreate);
      await this.loadItems();
    }
  }

  private async resolveEntityTypeForQuery(query: SparkQuery): Promise<{ entityType: EntityType | null; entityTypes: EntityType[] }> {
    const singularName = this.singularize(query.contextProperty);
    const entityTypes = await this.sparkService.getEntityTypes();
    const type = entityTypes.find(t =>
      t.name === query.contextProperty ||
      t.name === singularName ||
      t.clrType.endsWith(singularName)
    );
    return { entityType: type || null, entityTypes };
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

  async loadItems(): Promise<void> {
    const currentQuery = this.query();
    if (!currentQuery) return;
    try {
      const sortDirection = this.settings.sortDirection === 'descending' ? 'desc' : 'asc';
      const items = await this.sparkService.executeQuery(
        currentQuery.id,
        this.settings.sortProperty || undefined,
        this.settings.sortProperty ? sortDirection : undefined
      );
      this.errorMessage.set(null);
      this.allItems.set(items);
      this.currentSortProperty = this.settings.sortProperty;
      this.currentSortDirection = this.settings.sortDirection;
      this.applyFilter();
    } catch (e: any) {
      this.errorMessage.set(e.error?.error || e.message || 'An unexpected error occurred');
      this.allItems.set([]);
      this.applyFilter();
    }
  }

  onSettingsChange(): void {
    const sortChanged =
      this.settings.sortProperty !== this.currentSortProperty ||
      this.settings.sortDirection !== this.currentSortDirection;

    if (sortChanged) {
      this.settings.page.selected = 1;
      this.loadItems();
    } else {
      this.applyFilter();
    }
  }

  onSearchChange(): void {
    this.settings.page.selected = 1;
    this.applyFilter();
  }

  applyFilter(): void {
    let filteredItems = this.allItems();

    if (this.searchTerm.trim()) {
      const term = this.searchTerm.toLowerCase().trim();
      filteredItems = filteredItems.filter(item => {
        if (item.name?.toLowerCase().includes(term)) return true;
        if (item.breadcrumb?.toLowerCase().includes(term)) return true;
        return item.attributes.some(attr => {
          const value = attr.breadcrumb || attr.value;
          if (value == null) return false;
          return String(value).toLowerCase().includes(term);
        });
      });
    }

    const totalPages = Math.ceil(filteredItems.length / this.settings.perPage.selected) || 1;
    this.paginationData.set({
      data: filteredItems,
      totalRecords: filteredItems.length,
      totalPages: totalPages,
      perPage: this.settings.perPage.selected,
      page: this.settings.page.selected
    });

    this.settings.page.values = Array.from({ length: totalPages }, (_, i) => i + 1);

    if (this.settings.page.selected > totalPages) {
      this.settings.page.selected = 1;
    }
  }

  clearSearch(): void {
    this.searchTerm = '';
    this.onSearchChange();
  }

  visibleAttributes = computed(() => {
    return this.entityType()?.attributes
      .filter(a => a.isVisible && hasShowedOnFlag(a.showedOn, ShowedOn.Query))
      .sort((a, b) => a.order - b.order) || [];
  });

  getColumnTemplate(attr: EntityAttributeDefinition): TemplateRef<SparkColumnTemplateContext> | null {
    if (!this.columnTemplates) return null;
    const byName = this.columnTemplates.find(t => t.name() === attr.name);
    if (byName) return byName.template;
    const byType = this.columnTemplates.find(t => t.name() === attr.dataType);
    if (byType) return byType.template;
    return null;
  }

  getColumnTemplateContext(item: PersistentObject, attr: EntityAttributeDefinition): SparkColumnTemplateContext {
    const itemAttr = item.attributes.find(a => a.name === attr.name);
    return {
      $implicit: item,
      attr,
      value: itemAttr?.value
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

  onRowClick(item: PersistentObject): void {
    this.rowClicked.emit(item);
    const et = this.entityType();
    if (et) {
      this.router.navigate(['/po', et.alias || et.id, item.id]);
    }
  }

  onCreate(): void {
    this.createClicked.emit();
    const et = this.entityType();
    if (et) {
      this.router.navigate(['/po', et.alias || et.id, 'new']);
    }
  }
}
