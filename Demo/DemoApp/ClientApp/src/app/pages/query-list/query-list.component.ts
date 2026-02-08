import { Component, OnInit, ChangeDetectionStrategy, ChangeDetectorRef, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { BsDatatableModule, DatatableSettings } from '@mintplayer/ng-bootstrap/datatable';
import { BsFormModule } from '@mintplayer/ng-bootstrap/form';
import { BsInputGroupComponent } from '@mintplayer/ng-bootstrap/input-group';
import { PaginationResponse } from '@mintplayer/pagination';
import { SparkService } from '../../core/services/spark.service';
import { IconComponent } from '../../components/icon/icon.component';
import { EntityType, EntityAttributeDefinition, LookupReference, PersistentObject, SparkQuery } from '../../core/models';
import { ShowedOn, hasShowedOnFlag } from '../../core/models/showed-on';
import { switchMap, forkJoin, of } from 'rxjs';

@Component({
  selector: 'app-query-list',
  imports: [CommonModule, FormsModule, RouterModule, BsDatatableModule, BsFormModule, BsInputGroupComponent, IconComponent],
  templateUrl: './query-list.component.html',
  styleUrl: './query-list.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export default class QueryListComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly sparkService = inject(SparkService);
  private readonly cdr = inject(ChangeDetectorRef);

  query: SparkQuery | null = null;
  entityType: EntityType | null = null;
  allEntityTypes: EntityType[] = [];
  allItems: PersistentObject[] = [];
  lookupReferenceOptions: Record<string, LookupReference> = {};
  paginationData: PaginationResponse<PersistentObject> | undefined = undefined;
  searchTerm: string = '';
  settings: DatatableSettings = new DatatableSettings({
    perPage: { values: [10, 25, 50], selected: 10 },
    page: { values: [1], selected: 1 },
    sortProperty: '',
    sortDirection: 'ascending'
  });
  private currentSortProperty = '';
  private currentSortDirection = '';

  ngOnInit(): void {
    this.route.paramMap.pipe(
      switchMap(params => {
        const queryId = params.get('queryId');
        if (!queryId) return of(null);
        return this.sparkService.getQuery(queryId);
      }),
      switchMap(query => {
        if (!query) return of({ query: null, entityType: null, entityTypes: [] });
        this.query = query;
        const singularName = this.singularize(query.contextProperty);
        return forkJoin({
          query: of(query),
          entityTypes: this.sparkService.getEntityTypes()
        }).pipe(
          switchMap(result => {
            const type = result.entityTypes.find(t =>
              t.name === query.contextProperty ||
              t.name === singularName ||
              t.clrType.endsWith(singularName)
            );
            return of({
              query: result.query,
              entityType: type || null,
              entityTypes: result.entityTypes
            });
          })
        );
      })
    ).subscribe(result => {
      if (result?.entityType) {
        this.entityType = result.entityType;
        this.allEntityTypes = result.entityTypes;
        this.settings = new DatatableSettings({
          perPage: { values: [10, 25, 50], selected: 10 },
          page: { values: [1], selected: 1 },
          sortProperty: this.query?.sortBy || '',
          sortDirection: this.query?.sortDirection === 'desc' ? 'descending' : 'ascending'
        });
        this.loadLookupReferenceOptions();
        this.cdr.markForCheck();
        this.loadItems();
      }
    });
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

  loadItems(): void {
    if (!this.query) return;
    const sortDirection = this.settings.sortDirection === 'descending' ? 'desc' : 'asc';
    this.sparkService.executeQuery(
      this.query.id,
      this.settings.sortProperty || undefined,
      this.settings.sortProperty ? sortDirection : undefined
    ).subscribe(items => {
      this.allItems = items;
      this.currentSortProperty = this.settings.sortProperty;
      this.currentSortDirection = this.settings.sortDirection;
      this.applyFilter();
    });
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

  getVisibleAttributes() {
    return this.entityType?.attributes
      .filter(a => a.isVisible && hasShowedOnFlag(a.showedOn, ShowedOn.Query))
      .sort((a, b) => a.order - b.order) || [];
  }

  private loadLookupReferenceOptions(): void {
    const lookupAttrs = this.getVisibleAttributes().filter(a => a.lookupReferenceType);
    if (lookupAttrs.length === 0) return;

    const lookupNames = [...new Set(lookupAttrs.map(a => a.lookupReferenceType!))];
    const queries: Record<string, ReturnType<typeof this.sparkService.getLookupReference>> = {};
    lookupNames.forEach(name => {
      queries[name] = this.sparkService.getLookupReference(name);
    });

    forkJoin(queries).subscribe(results => {
      this.lookupReferenceOptions = results;
      this.cdr.markForCheck();
    });
  }

  getAttributeValue(item: PersistentObject, attrName: string): any {
    const attr = item.attributes.find(a => a.name === attrName);
    if (!attr) return '';

    // For Reference attributes, breadcrumb is resolved on backend
    if (attr.breadcrumb) return attr.breadcrumb;

    // For AsDetail attributes, format using displayFormat
    const attrDef = this.entityType?.attributes.find(a => a.name === attrName);
    if (attrDef?.dataType === 'AsDetail' && attr.value && typeof attr.value === 'object') {
      return this.formatAsDetailValue(attrDef, attr.value);
    }

    // For LookupReference attributes, resolve to translated display name
    if (attrDef?.lookupReferenceType && attr.value != null && attr.value !== '') {
      const lookupRef = this.lookupReferenceOptions[attrDef.lookupReferenceType];
      if (lookupRef) {
        const option = lookupRef.values.find(v => v.key === String(attr.value));
        if (option) {
          const lang = navigator.language?.split('-')[0] || 'en';
          return option.translations[lang] || option.translations['en'] || Object.values(option.translations)[0] || option.key;
        }
      }
    }

    return attr.value ?? '';
  }

  private formatAsDetailValue(attrDef: EntityAttributeDefinition, value: Record<string, any>): string {
    // Find the AsDetail entity type
    const asDetailType = this.allEntityTypes.find(t => t.clrType === attrDef.asDetailType);

    // 1. Try displayFormat (template with {PropertyName} placeholders)
    if (asDetailType?.displayFormat) {
      const result = this.resolveDisplayFormat(asDetailType.displayFormat, value);
      if (result && result.trim()) return result;
    }

    // 2. Try displayAttribute (single property name)
    if (asDetailType?.displayAttribute && value[asDetailType.displayAttribute]) {
      return value[asDetailType.displayAttribute];
    }

    // 3. Fallback to common property names
    const displayProps = ['Name', 'Title', 'Street', 'name', 'title'];
    for (const prop of displayProps) {
      if (value[prop]) return value[prop];
    }

    return '(object)';
  }

  private resolveDisplayFormat(format: string, data: Record<string, any>): string {
    return format.replace(/\{(\w+)\}/g, (match, propertyName) => {
      const value = data[propertyName];
      return value != null ? String(value) : '';
    });
  }

  onRowClick(item: PersistentObject): void {
    if (this.entityType) {
      this.router.navigate(['/po', this.entityType.id, item.id]);
    }
  }

  onCreate(): void {
    if (this.entityType) {
      this.router.navigate(['/po', this.entityType.id, 'new']);
    }
  }
}
