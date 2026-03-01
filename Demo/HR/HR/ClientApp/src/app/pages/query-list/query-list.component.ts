import { Component, ChangeDetectionStrategy, ChangeDetectorRef, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsDatatableComponent, BsDatatableColumnDirective, BsRowTemplateDirective, DatatableSettings } from '@mintplayer/ng-bootstrap/datatable';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsContainerComponent } from '@mintplayer/ng-bootstrap/container';
import { BsGridComponent, BsGridRowDirective, BsGridColumnDirective } from '@mintplayer/ng-bootstrap/grid';
import { BsInputGroupComponent } from '@mintplayer/ng-bootstrap/input-group';
import { PaginationResponse } from '@mintplayer/pagination';
import { SparkService, SparkIconComponent, ResolveTranslationPipe, TranslateKeyPipe, AttributeValuePipe, EntityType, EntityAttributeDefinition, LookupReference, PersistentObject, SparkQuery, ShowedOn, hasShowedOnFlag } from '@mintplayer/ng-spark';

@Component({
  selector: 'app-query-list',
  imports: [CommonModule, FormsModule, RouterModule, BsAlertComponent, BsContainerComponent, BsDatatableComponent, BsDatatableColumnDirective, BsRowTemplateDirective, BsFormComponent, BsFormControlDirective, BsGridComponent, BsGridRowDirective, BsGridColumnDirective, BsInputGroupComponent, SparkIconComponent, ResolveTranslationPipe, TranslateKeyPipe, AttributeValuePipe],
  templateUrl: './query-list.component.html',
  styleUrl: './query-list.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export default class QueryListComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly sparkService = inject(SparkService);
  private readonly cdr = inject(ChangeDetectorRef);

  colors = Color;
  errorMessage = signal<string | null>(null);
  query = signal<SparkQuery | null>(null);
  entityType = signal<EntityType | null>(null);
  allEntityTypes = signal<EntityType[]>([]);
  allItems: PersistentObject[] = [];
  lookupReferenceOptions: Record<string, LookupReference> = {};
  paginationData: PaginationResponse<PersistentObject> | undefined = undefined;
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
    this.route.paramMap.subscribe(params => this.onParamsChange(params));
  }

  private async onParamsChange(params: any): Promise<void> {
    const queryId = params.get('queryId');
    const typeParam = params.get('type');

    let resolvedQuery: SparkQuery | null = null;
    let resolvedEntityType: EntityType | null = null;
    let entityTypes: EntityType[] = [];

    if (queryId) {
      // Entry via /query/:queryId route
      const q = await this.sparkService.getQuery(queryId);
      if (q) {
        resolvedQuery = q;
        const singularName = this.singularize(q.contextProperty);
        entityTypes = await this.sparkService.getEntityTypes();
        resolvedEntityType = entityTypes.find(t =>
          t.name === q.contextProperty ||
          t.name === singularName ||
          t.clrType.endsWith(singularName)
        ) || null;
      }
    } else if (typeParam) {
      // Entry via /po/:type route
      entityTypes = await this.sparkService.getEntityTypes();
      const et = entityTypes.find(t => t.id === typeParam || t.alias === typeParam);
      if (et) {
        resolvedEntityType = et;
        const queries = await this.sparkService.getQueries();
        const singularName = et.name;
        resolvedQuery = queries.find(q => {
          const contextSingular = this.singularize(q.contextProperty);
          return q.contextProperty === singularName ||
            contextSingular === singularName ||
            q.contextProperty === singularName + 's';
        }) || null;
      }
    }

    this.query.set(resolvedQuery);
    this.entityType.set(resolvedEntityType);
    this.allEntityTypes.set(entityTypes);

    if (resolvedEntityType) {
      this.settings = new DatatableSettings({
        perPage: { values: [10, 25, 50], selected: 10 },
        page: { values: [1], selected: 1 },
        sortProperty: resolvedQuery?.sortBy || '',
        sortDirection: resolvedQuery?.sortDirection === 'desc' ? 'descending' : 'ascending'
      });
      this.loadLookupReferenceOptions();
      this.cdr.markForCheck();

      const p = await this.sparkService.getPermissions(resolvedEntityType.id);
      this.canCreate.set(p.canCreate);
      this.cdr.markForCheck();

      await this.loadItems();
    }
  }

  private singularize(plural: string): string {
    // Handle irregular plurals
    const irregulars: { [key: string]: string } = {
      'People': 'Person',
      'Children': 'Child',
      'Men': 'Men',
      'Women': 'Woman'
    };
    if (irregulars[plural]) return irregulars[plural];

    // Handle regular plurals
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
    const q = this.query();
    if (!q) return;
    const sortDirection = this.settings.sortDirection === 'descending' ? 'desc' : 'asc';
    try {
      const items = await this.sparkService.executeQuery(
        q.id,
        this.settings.sortProperty || undefined,
        this.settings.sortProperty ? sortDirection : undefined
      );
      this.errorMessage.set(null);
      this.allItems = items;
      this.currentSortProperty = this.settings.sortProperty;
      this.currentSortDirection = this.settings.sortDirection;
      this.applyFilter();
    } catch (e) {
      const error = e as HttpErrorResponse;
      this.errorMessage.set(error.error?.error || error.message || 'An unexpected error occurred');
      this.allItems = [];
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
    // Reset to first page when searching
    this.settings.page.selected = 1;
    this.applyFilter();
  }

  applyFilter(): void {
    let filteredItems = this.allItems;

    // Apply search filter
    if (this.searchTerm.trim()) {
      const term = this.searchTerm.toLowerCase().trim();
      filteredItems = this.allItems.filter(item => {
        // Search in name
        if (item.name?.toLowerCase().includes(term)) return true;
        // Search in breadcrumb
        if (item.breadcrumb?.toLowerCase().includes(term)) return true;
        // Search in all attribute values
        return item.attributes.some(attr => {
          const value = attr.breadcrumb || attr.value;
          if (value == null) return false;
          return String(value).toLowerCase().includes(term);
        });
      });
    }

    const totalPages = Math.ceil(filteredItems.length / this.settings.perPage.selected) || 1;
    this.paginationData = {
      data: filteredItems,
      totalRecords: filteredItems.length,
      totalPages: totalPages,
      perPage: this.settings.perPage.selected,
      page: this.settings.page.selected
    };

    // Update page values for pagination
    this.settings.page.values = Array.from({ length: totalPages }, (_, i) => i + 1);

    // Ensure current page is valid
    if (this.settings.page.selected > totalPages) {
      this.settings.page.selected = 1;
    }

    this.cdr.markForCheck();
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

    this.lookupReferenceOptions = Object.fromEntries(entries);
    this.cdr.markForCheck();
  }

  onRowClick(item: PersistentObject): void {
    const et = this.entityType();
    if (et) {
      this.router.navigate(['/po', et.alias || et.id, item.id]);
    }
  }

  onCreate(): void {
    const et = this.entityType();
    if (et) {
      this.router.navigate(['/po', et.alias || et.id, 'new']);
    }
  }
}
