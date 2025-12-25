import { Component, OnInit, ChangeDetectionStrategy, ChangeDetectorRef, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { BsDatatableModule, DatatableSettings } from '@mintplayer/ng-bootstrap/datatable';
import { PaginationResponse } from '@mintplayer/pagination';
import { SparkService } from '../../core/services/spark.service';
import { EntityType, PersistentObject, SparkQuery } from '../../core/models';
import { switchMap, forkJoin, of } from 'rxjs';

@Component({
  selector: 'app-query-list',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, BsDatatableModule],
  templateUrl: './query-list.component.html',
  styleUrl: './query-list.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export default class QueryListComponent implements OnInit {
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  private sparkService = inject(SparkService);
  private cdr = inject(ChangeDetectorRef);

  query: SparkQuery | null = null;
  entityType: EntityType | null = null;
  allItems: PersistentObject[] = [];
  paginationData: PaginationResponse<PersistentObject> | undefined = undefined;
  searchTerm: string = '';
  settings: DatatableSettings = new DatatableSettings({
    perPage: { values: [10, 25, 50], selected: 10 },
    page: { values: [1], selected: 1 },
    sortProperty: '',
    sortDirection: 'ascending'
  });

  ngOnInit(): void {
    this.route.paramMap.pipe(
      switchMap(params => {
        const queryId = params.get('queryId');
        if (!queryId) return of(null);
        return this.sparkService.getQuery(queryId);
      }),
      switchMap(query => {
        if (!query) return of({ query: null, entityType: null });
        this.query = query;
        const singularName = this.singularize(query.contextProperty);
        return forkJoin({
          query: of(query),
          entityType: this.sparkService.getEntityTypes().pipe(
            switchMap(types => {
              const type = types.find(t =>
                t.name === query.contextProperty ||
                t.name === singularName ||
                t.clrType.endsWith(singularName)
              );
              return of(type || null);
            })
          )
        });
      })
    ).subscribe(result => {
      if (result?.entityType) {
        this.entityType = result.entityType;
        this.settings = new DatatableSettings({
          perPage: { values: [10, 25, 50], selected: 10 },
          page: { values: [1], selected: 1 },
          sortProperty: this.query?.sortBy || '',
          sortDirection: this.query?.sortDirection === 'desc' ? 'descending' : 'ascending'
        });
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
    if (!this.entityType) return;
    this.sparkService.list(this.entityType.id).subscribe(items => {
      this.allItems = items;
      this.applyFilter();
    });
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
      .filter(a => a.isVisible)
      .sort((a, b) => a.order - b.order) || [];
  }

  getAttributeValue(item: PersistentObject, attrName: string): any {
    const attr = item.attributes.find(a => a.name === attrName);
    return attr?.breadcrumb || attr?.value || '';
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
